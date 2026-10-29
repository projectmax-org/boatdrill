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
        Directory.CreateDirectory(scratchDirectory);
        var work = Directory.CreateDirectory(Path.Combine(scratchDirectory, Scratch.Prefix + "scan-" + Guid.NewGuid().ToString("N")[..8]));
        try
        {
            return await ScanInAsync(sources, destinations, work.FullName, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            Scratch.TryDelete(work.FullName);
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

            progress?.Report(new ScanProgress("Checking " + destination.Name, 0, 0));
            var probe = await adapter.ProbeAsync(destination, ct).ConfigureAwait(false);
            if (probe.Reachable) reachable.Add((destination, adapter));
            else unreachable.Add($"{destination.Name}: {probe.Detail}");
        }

        var summaries = new List<OnlyHereSource>();
        var folders = new Dictionary<(string Source, string Folder), (int Files, long Bytes)>();
        var largest = new PriorityQueue<OnlyHereFile, long>();
        long checkedFiles = 0, onlyHere = 0;

        // Nothing to compare with when no backup could be reached; the report says so instead.
        foreach (var source in reachable.Count == 0 ? [] : sources.DistinctBy(s => s.Id))
        {
            ct.ThrowIfCancellationRequested();

            // A drill-sized sample is enough for each adapter to work out where this source's copies live.
            progress?.Report(new ScanProgress("Finding " + source.Name + " in the backups", checkedFiles, onlyHere));
            var probeSample = new Sampler(clock: _clock).Take(source);
            var bindings = new List<(IDestinationAdapter Adapter, SourceBinding Binding)>();
            foreach (var (destination, adapter) in reachable)
            {
                bindings.Add((adapter, await adapter.BindAsync(destination, probeSample, scratchDirectory, BindPurpose.ListOnly, [], ct).ConfigureAwait(false)));
            }

            var summary = new OnlyHereSource(source.Id, source.Name, Path.GetFullPath(source.RootPath));
            long files = 0, bytes = 0, noCopy = 0, noCopyBytes = 0, older = 0, olderBytes = 0;
            foreach (var file in SourceFiles.Enumerate(source))
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

                if (++checkedFiles % 500 == 0) progress?.Report(new ScanProgress("Looking through " + source.Name, checkedFiles, onlyHere));
            }

            summaries.Add(summary with
            {
                Files = files,
                Bytes = bytes,
                NoCopyFiles = noCopy,
                NoCopyBytes = noCopyBytes,
                OlderFiles = older,
                OlderBytes = olderBytes,
            });
        }

        var report = new OnlyHereReport
        {
            StartedUtc = started,
            FinishedUtc = _clock.GetUtcNow(),
            Checked = reachable.Select(r => r.Destination.Name).ToList(),
            Unreachable = unreachable,
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
        if (r.Checked.Count == 0)
        {
            return r.Unreachable.Count == 0
                ? "There are no backups set up to compare with."
                : "None of the backups could be reached, so nothing was compared.";
        }

        var total = r.Sources.Sum(s => s.Files);
        if (total == 0) return "There are no files in the folders being protected.";
        if (r.OnlyHereFiles == 0)
        {
            return $"All {total:N0} {(total == 1 ? "file has" : "files have")} a current copy in at least one backup.";
        }

        var noCopy = r.Sources.Sum(s => s.NoCopyFiles);
        var older = r.Sources.Sum(s => s.OlderFiles);
        var parts = new List<string>();
        if (noCopy > 0) parts.Add($"{noCopy:N0} {(noCopy == 1 ? "has" : "have")} no copy in any backup");
        if (older > 0) parts.Add($"{older:N0} changed after {(older == 1 ? "its" : "their")} last copy");

        var lead = $"{r.OnlyHereFiles:N0} of {total:N0} {(total == 1 ? "file" : "files")} ({Humanize.Bytes(r.OnlyHereBytes)}) exist only on this PC: {string.Join(" and ", parts)}.";
        return r.Unreachable.Count == 0 ? lead : $"{lead} {Humanize.Plural(r.Unreachable.Count, "backup")} could not be reached and {(r.Unreachable.Count == 1 ? "was" : "were")} not looked in.";
    }
}
