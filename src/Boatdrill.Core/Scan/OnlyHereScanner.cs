using Boatdrill.Core.Adapters;
using Boatdrill.Core.Drill;
using Boatdrill.Core.Sampling;
using Boatdrill.Core.Text;

namespace Boatdrill.Core.Scan;

/// <summary>
/// Goes through every file in the sources, not a sample, and looks each one up in every backup that can be
/// reached. A file whose current version none of them holds exists only on this PC. Lookups use names and
/// dates only, so a scan reads directory listings (and a restic snapshot's index), never file contents.
/// </summary>
public sealed class OnlyHereScanner
{
    public const int ListedFolders = 50;
    public const int ListedFiles = 50;

    /// <summary>FAT keeps write times to two seconds; a copy that close in time is the same version.</summary>
    private static readonly TimeSpan WriteTimeTolerance = TimeSpan.FromSeconds(2);

    private readonly IReadOnlyDictionary<DestinationKind, IDestinationAdapter> _adapters;
    private readonly TimeProvider _clock;

    public OnlyHereScanner(IEnumerable<IDestinationAdapter>? adapters = null, TimeProvider? clock = null)
    {
        _adapters = (adapters ?? DrillRunner.DefaultAdapters()).ToDictionary(a => a.Kind);
        _clock = clock ?? TimeProvider.System;
    }

    private enum Copy
    {
        None,
        Older,
        Current,
    }

    /// <param name="scratchDirectory">
    /// Where adapters may keep lists while they work, in a folder of the scan's own that is removed afterwards. A scan restores nothing.
    /// </param>
    public async Task<OnlyHereReport> ScanAsync(IReadOnlyList<Source> sources, IReadOnlyList<Destination> destinations, string scratchDirectory,
        IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        Scratch.MakeFolder(scratchDirectory);
        var work = Scratch.Create(scratchDirectory, Scratch.Prefix + "scan-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            return await ScanInAsync(sources, destinations, work.FullName, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            work.Dispose();
        }
    }

    private async Task<OnlyHereReport> ScanInAsync(IReadOnlyList<Source> sources, IReadOnlyList<Destination> destinations, string scratchDirectory,
        IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var started = _clock.GetUtcNow();
        var reachable = new List<(Destination Destination, IDestinationAdapter Adapter)>();
        var unreachable = new List<string>();
        foreach (var destination in destinations)
        {
            ct.ThrowIfCancellationRequested();
            if (!_adapters.TryGetValue(destination.Kind, out var adapter)) throw new NotSupportedException($"No adapter for {destination.Kind}.");

            progress?.Report(new ScanProgress(Language.Pick("Checking " + destination.Name, destination.Name + " 확인 중"), 0, 0));
            var probe = await adapter.ProbeAsync(destination, ct).ConfigureAwait(false);
            if (probe.Reachable) reachable.Add((destination, adapter));
            else unreachable.Add($"{destination.Name}: {probe.Detail}");
        }

        var summaries = new List<OnlyHereSource>();
        var lookedIn = new HashSet<string>(StringComparer.Ordinal);
        var notLookedIn = new Dictionary<string, string>(StringComparer.Ordinal);
        var notCompared = new List<string>();
        var notFound = new List<OnlyHereSource>();
        var folders = new Dictionary<(string Source, string Folder), (int Files, long Bytes)>();
        var largest = new PriorityQueue<OnlyHereFile, long>();
        long checkedFiles = 0, onlyHere = 0;

        // Nothing to compare with when no backup could be reached; the report says so instead.
        foreach (var source in reachable.Count == 0 ? [] : sources.DistinctBy(s => s.Id))
        {
            ct.ThrowIfCancellationRequested();

            // A drill-sized sample is enough for each adapter to work out where this source's copies live.
            progress?.Report(new ScanProgress(Language.Pick("Finding " + source.Name + " in the backups", $"백업에서 {source.Name} 찾는 중"), checkedFiles, onlyHere));
            var probeSample = new Sampler(clock: _clock).Take(source);
            var summary = new OnlyHereSource(source.Id, source.Name, Path.GetFullPath(source.RootPath));

            // A folder that is not there has no files to look up, which is not every file in it having a copy: the
            // report names the folder instead of counting it.
            if (probeSample.Missing)
            {
                notFound.Add(summary);
                continue;
            }

            var bindings = new List<(IDestinationAdapter Adapter, SourceBinding Binding)>();
            foreach (var (destination, adapter) in reachable)
            {
                var binding = await adapter.BindAsync(destination, probeSample, scratchDirectory, BindPurpose.ListOnly, [], ct).ConfigureAwait(false);

                // A backup whose files could not be listed holds an unknown share of them, not none: it is not looked in.
                if (binding.CouldNotLook)
                {
                    notLookedIn.TryAdd(destination.Name, binding.Problem ?? string.Empty);
                    continue;
                }

                lookedIn.Add(destination.Name);
                bindings.Add((adapter, binding));
            }

            // With no backup to look in, every file would count as having no copy anywhere; the report names the folder instead.
            if (bindings.Count == 0)
            {
                notCompared.Add(source.Name);
                continue;
            }

            // A file whose bytes are not on this PC is left out, as a drill leaves it out: looking it up would say
            // nothing about what is only here. It is counted, so that the report does not read as if there were none.
            long files = 0, bytes = 0, noCopy = 0, noCopyBytes = 0, older = 0, olderBytes = 0, onlineOnly = 0;
            foreach (var file in SourceFiles.Enumerate(source, notHere: () => onlineOnly++))
            {
                ct.ThrowIfCancellationRequested();
                files++;
                bytes += file.Length;

                var copy = await BestCopyAsync(bindings, file, ct).ConfigureAwait(false);
                if (copy != Copy.Current)
                {
                    onlyHere++;
                    if (copy == Copy.Older)
                    {
                        older++;
                        olderBytes += file.Length;
                    }
                    else
                    {
                        noCopy++;
                        noCopyBytes += file.Length;
                    }

                    var folder = (source.Name, Path.GetDirectoryName(file.RelativePath) ?? string.Empty);
                    var (count, size) = folders.GetValueOrDefault(folder);
                    folders[folder] = (count + 1, size + file.Length);

                    largest.Enqueue(new OnlyHereFile(source.Name, file.RelativePath, file.Length, file.LastWriteUtc, copy == Copy.Older), file.Length);
                    if (largest.Count > ListedFiles) largest.Dequeue();
                }

                if (++checkedFiles % 500 == 0) progress?.Report(new ScanProgress(Language.Pick("Looking through " + source.Name, source.Name + " 살펴보는 중"), checkedFiles, onlyHere));
            }

            // Gone since it was sampled, as when its drive is unplugged while a backup is being listed: none of its
            // files were looked up either.
            if (files == 0 && !Directory.Exists(summary.RootPath))
            {
                notFound.Add(summary);
                continue;
            }

            summaries.Add(summary with
            {
                Files = files,
                Bytes = bytes,
                NoCopyFiles = noCopy,
                NoCopyBytes = noCopyBytes,
                OlderFiles = older,
                OlderBytes = olderBytes,
                OnlineOnlyFiles = onlineOnly,
            });
        }

        var report = new OnlyHereReport
        {
            StartedUtc = started,
            FinishedUtc = _clock.GetUtcNow(),
            // A backup counts as looked in when at least one folder's files could be listed in it.
            Checked = reachable.Select(r => r.Destination.Name).Where(name => lookedIn.Contains(name) || !notLookedIn.ContainsKey(name)).ToList(),
            Unreachable = [.. unreachable, .. notLookedIn.Select(n => $"{n.Key}: {n.Value}")],
            NotCompared = notCompared,
            NotFound = notFound,
            Sources = summaries,
            Folders = folders
                .OrderByDescending(f => f.Value.Bytes)
                .ThenBy(f => f.Key.Folder, StringComparer.OrdinalIgnoreCase)
                .Take(ListedFolders)
                .Select(f => new OnlyHereFolder(f.Key.Source, f.Key.Folder, f.Value.Files, f.Value.Bytes))
                .ToList(),
            Largest = largest.UnorderedItems.Select(i => i.Element).OrderByDescending(f => f.Length).ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).ToList(),
            Sentence = string.Empty,
        };
        return report with { Sentence = Sentence(report) };
    }

    /// <summary>The best copy any backup holds of a file: one at least as new as the file, an older one, or none.</summary>
    private static async Task<Copy> BestCopyAsync(List<(IDestinationAdapter Adapter, SourceBinding Binding)> bindings, SampledFile file, CancellationToken ct)
    {
        var best = Copy.None;
        foreach (var (adapter, binding) in bindings)
        {
            var copy = await adapter.LocateAsync(binding, file.RelativePath, ct).ConfigureAwait(false);

            // A cloud placeholder counts: the bytes are in the cloud, which is a copy that is not on this PC.
            if (copy.State is not (CopyState.Found or CopyState.Placeholder)) continue;
            if (copy.LastWriteUtc is not { } written || written >= file.LastWriteUtc - WriteTimeTolerance) return Copy.Current;
            best = Copy.Older;
        }

        return best;
    }

    public static string Sentence(OnlyHereReport r)
    {
        if (Language.IsKorean) return KoreanSentence(r);

        if (r.Checked.Count == 0)
        {
            return r.Unreachable.Count == 0
                ? "There are no backups set up to compare with."
                : "None of the backups could be looked in, so nothing was compared.";
        }

        // A folder that no backup could be looked in for is not counted, and must not pass for one whose files have copies.
        // Neither must a folder that was not there, which is not an empty one either.
        var notCompared = r.NotCompared.Count == 0 ? string.Empty : $" {string.Join(", ", r.NotCompared)} could not be compared with any backup.";
        var notFound = NotFoundSentence(r);
        var onlineOnly = r.OnlineOnlyFiles == 0 ? string.Empty : " " + OnlineOnlySentence(r.OnlineOnlyFiles);
        var total = r.Sources.Sum(s => s.Files);
        if (total == 0)
        {
            // Folders whose files are all online only are not folders with no files in them.
            var leftOut = (notFound + notCompared + onlineOnly).TrimStart();
            if (leftOut.Length == 0) return "There are no files in the folders being protected.";
            return r.OnlineOnlyFiles == 0 ? leftOut : leftOut + " There was no other file to look up.";
        }

        if (r.OnlyHereFiles == 0)
        {
            // Beside a folder that was not found, that is said first and "all" is said of the files that were looked up:
            // it must never read as every file there is.
            var all = notFound.Length == 0
                ? $"All {total:N0} {(total == 1 ? "file has" : "files have")} a current copy in at least one backup."
                : $"{notFound} All {total:N0} {(total == 1 ? "file that was looked up has" : "files that were looked up have")} a current copy in at least one backup.";
            return all + notCompared + onlineOnly;
        }

        var noCopy = r.Sources.Sum(s => s.NoCopyFiles);
        var older = r.Sources.Sum(s => s.OlderFiles);
        var parts = new List<string>();
        if (noCopy > 0) parts.Add($"{noCopy:N0} {(noCopy == 1 ? "has" : "have")} no copy in any backup");
        if (older > 0) parts.Add($"{older:N0} changed after {(older == 1 ? "its" : "their")} last copy");

        var lead = $"{r.OnlyHereFiles:N0} of {total:N0} {(total == 1 ? "file" : "files")} ({Humanize.Bytes(r.OnlyHereBytes)}) exist only on this PC: {string.Join(" and ", parts)}.";
        if (r.Unreachable.Count > 0) lead += $" {Humanize.Plural(r.Unreachable.Count, "backup")} could not be looked in and may hold copies of some of these files.";
        return lead + notCompared + (notFound.Length == 0 ? string.Empty : " " + notFound) + onlineOnly;
    }

    /// <summary>
    /// The files left out because their bytes are not on this PC, as a sentence. They are in the cloud, so they are
    /// not what would be lost with this PC, but a scan that says nothing of them reads as if they were not there.
    /// </summary>
    private static string OnlineOnlySentence(long count) => Language.IsKorean
        ? $"온라인 전용 파일 {count:N0}개는 이 PC에 없어 살펴보지 않았습니다."
        : $"{count:N0} online-only {(count == 1 ? "file is" : "files are")} not on this PC and {(count == 1 ? "was" : "were")} left out.";

    /// <summary>
    /// The protected folders that were not there, as a sentence; empty when every one was found. One folder comes with
    /// its path, since that is where the user has to look.
    /// </summary>
    private static string NotFoundSentence(OnlyHereReport r)
    {
        if (r.NotFound.Count == 0) return string.Empty;

        var names = Language.List(r.NotFound.Select(s => s.SourceName).ToList());
        if (Language.IsKorean)
        {
            // "사진 폴더(C:\Users\you\Pictures)를": the particle goes with the name, not with the path in brackets.
            var folders = r.NotFound.Count == 1 ? $"{Korean.Folder(names)}({r.NotFound[0].RootPath})" : Korean.Folder(names);
            return $"{Korean.Object(folders)} 찾을 수 없어 살펴보지 못했습니다.";
        }

        return r.NotFound.Count == 1
            ? $"{names} was not found at {r.NotFound[0].RootPath}, so none of its files were looked up."
            : $"{names} were not found, so none of their files were looked up.";
    }

    private static string KoreanSentence(OnlyHereReport r)
    {
        if (r.Checked.Count == 0)
        {
            return r.Unreachable.Count == 0
                ? "비교할 백업이 아직 설정되지 않았습니다."
                : "살펴볼 수 있는 백업이 없어 아무것도 비교하지 못했습니다.";
        }

        var notCompared = r.NotCompared.Count == 0 ? string.Empty : $" {Korean.Topic(string.Join(", ", r.NotCompared))} 어느 백업과도 비교하지 못했습니다.";
        var notFound = NotFoundSentence(r);
        var onlineOnly = r.OnlineOnlyFiles == 0 ? string.Empty : " " + OnlineOnlySentence(r.OnlineOnlyFiles);
        var total = r.Sources.Sum(s => s.Files);
        if (total == 0)
        {
            var leftOut = (notFound + notCompared + onlineOnly).TrimStart();
            if (leftOut.Length == 0) return "보호하는 폴더에 파일이 없습니다.";
            return r.OnlineOnlyFiles == 0 ? leftOut : leftOut + " 그 밖에 살펴볼 파일은 없었습니다.";
        }

        if (r.OnlyHereFiles == 0)
        {
            var all = notFound.Length == 0
                ? $"파일 {total:N0}개 모두 적어도 한 백업에 최신 사본이 있습니다."
                : $"{notFound} 살펴본 파일 {total:N0}개 모두 적어도 한 백업에 최신 사본이 있습니다.";
            return all + notCompared + onlineOnly;
        }

        var noCopy = r.Sources.Sum(s => s.NoCopyFiles);
        var older = r.Sources.Sum(s => s.OlderFiles);
        var sentences = new List<string> { $"파일 {total:N0}개 중 {r.OnlyHereFiles:N0}개({Humanize.Bytes(r.OnlyHereBytes)})가 이 PC에만 있습니다." };
        if (noCopy > 0) sentences.Add($"{noCopy:N0}개는 어느 백업에도 사본이 없습니다.");
        if (older > 0) sentences.Add($"{older:N0}개는 마지막으로 복사된 뒤에 바뀌었습니다.");
        if (r.Unreachable.Count > 0) sentences.Add($"살펴보지 못한 백업 {r.Unreachable.Count:N0}개에 이 중 일부 파일의 사본이 있을 수 있습니다.");
        return string.Join(" ", sentences) + notCompared + (notFound.Length == 0 ? string.Empty : " " + notFound) + onlineOnly;
    }
}
