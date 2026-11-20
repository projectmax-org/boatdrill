using Boatdrill.Core.Adapters;
using Boatdrill.Core.Sampling;
using Boatdrill.Core.Text;
using Boatdrill.Core.Verification;

namespace Boatdrill.Core.Drill;

/// <summary>A full restore to rehearse: every file of one protected folder, or of a folder inside it, from one backup.</summary>
public sealed record RehearsalPlan(Source Source, Destination Destination, string ScratchDirectory)
{
    /// <summary>A folder inside the source, by its path below the source root; empty for the whole source.</summary>
    public string Folder { get; init; } = string.Empty;

    /// <summary>Most bytes fetched into scratch at once. A folder of copies is read one file at a time anyway.</summary>
    public long BatchBytes { get; init; } = RehearsalRunner.DefaultBatchBytes;
}

public sealed record RehearsalProgress(string Stage, long FilesDone, long FilesTotal, long BytesDone, long BytesTotal);

/// <summary>What a full-restore rehearsal found: every file accounted for, and how long restoring them took.</summary>
public sealed record RehearsalResult
{
    public string SourceId { get; init; } = string.Empty;

    public required string SourceName { get; init; }

    public required string SourceRoot { get; init; }

    /// <summary>The folder inside the source that was rehearsed; empty for the whole source.</summary>
    public string Folder { get; init; } = string.Empty;

    public required string DestinationId { get; init; }

    public required string DestinationName { get; init; }

    public DestinationKind Kind { get; init; }

    public required string RootPath { get; init; }

    public required DateTimeOffset StartedUtc { get; init; }

    public required DateTimeOffset FinishedUtc { get; init; }

    public bool Reachable { get; init; }

    public string? ProbeDetail { get; init; }

    /// <summary>Why the rehearsal did not start although the backup was there, such as too little scratch space.</summary>
    public string? Refusal { get; init; }

    public long Files { get; init; }

    public long Bytes { get; init; }

    public long Verified { get; init; }

    public long Outdated { get; init; }

    public long Corrupt { get; init; }

    public long Missing { get; init; }

    public long Placeholder { get; init; }

    public long Errors { get; init; }

    /// <summary>Files of the folder left out because they are online only: their bytes are not on this PC to compare with.</summary>
    public long OnlineOnly { get; init; }

    public long BytesRestored { get; init; }

    /// <summary>Time spent restoring and reading copies back: what a real restore of this folder would take.</summary>
    public TimeSpan RestoreTime { get; init; }

    /// <summary>The files that did not come back intact, worst first, at most <see cref="RehearsalRunner.MaxFindings"/>.</summary>
    public IReadOnlyList<FileFinding> Findings { get; init; } = [];

    public required string Sentence { get; init; }

    /// <summary>Every file came back intact. A rehearsal that restored nothing has not shown that.</summary>
    public bool AllIntact => Reachable && Refusal is null && Files > 0 && Verified == Files;
}

/// <summary>
/// Rehearses a full restore: every file of one folder is restored from one backup into the scratch folder, a
/// batch at a time, and compared with the original, so the answer covers every file rather than a sample, and
/// the time it took is how long a real restore would take. Asked for, never scheduled: it reads everything.
/// </summary>
public sealed class RehearsalRunner
{
    public const long DefaultBatchBytes = 1L << 30;
    public const int MaxFindings = 500;

    /// <summary>Kept free on the scratch drive beyond the largest batch, so a rehearsal never fills the disk.</summary>
    private const long SpareBytes = 256L * 1024 * 1024;

    private readonly IReadOnlyDictionary<DestinationKind, IDestinationAdapter> _adapters;
    private readonly TimeProvider _clock;
    private readonly Func<string, long?> _freeSpace;

    /// <param name="freeSpace">Free bytes on the drive holding a folder, or null when unknown; replaceable in tests.</param>
    public RehearsalRunner(IEnumerable<IDestinationAdapter>? adapters = null, TimeProvider? clock = null, Func<string, long?>? freeSpace = null)
    {
        _adapters = (adapters ?? DrillRunner.DefaultAdapters()).ToDictionary(a => a.Kind);
        _clock = clock ?? TimeProvider.System;
        _freeSpace = freeSpace ?? FreeSpace;
    }

    public async Task<RehearsalResult> RunAsync(RehearsalPlan plan, IProgress<RehearsalProgress>? progress = null, CancellationToken ct = default)
    {
        var started = _clock.GetUtcNow();
        var destination = plan.Destination;
        if (!_adapters.TryGetValue(destination.Kind, out var adapter)) throw new NotSupportedException($"No adapter for {destination.Kind}.");

        var result = new RehearsalResult
        {
            SourceId = plan.Source.Id,
            SourceName = plan.Source.Name,
            SourceRoot = Path.GetFullPath(plan.Source.RootPath),
            Folder = plan.Folder,
            DestinationId = destination.Id,
            DestinationName = destination.Name,
            Kind = destination.Kind,
            RootPath = destination.DisplayLocation,
            StartedUtc = started,
            FinishedUtc = started,
            Sentence = string.Empty,
        };

        progress?.Report(new RehearsalProgress(Language.Pick("Checking " + destination.Name, destination.Name + " 확인 중"), 0, 0, 0, 0));
        var probe = await adapter.ProbeAsync(destination, ct).ConfigureAwait(false);
        if (!probe.Reachable) return Finish(result with { ProbeDetail = probe.Detail });

        result = result with { Reachable = true, ProbeDetail = probe.Detail };

        // A folder that is not there would rehearse nothing; one outside the source would compare files elsewhere with themselves.
        if (FolderProblem(plan) is { } problem) return Finish(result with { Refusal = problem });

        progress?.Report(new RehearsalProgress(Language.Pick("Listing " + Label(result), Label(result) + " 목록 만드는 중"), 0, 0, 0, 0));
        long onlineOnly = 0;
        var files = SourceFiles.Enumerate(plan.Source, plan.Folder, () => onlineOnly++).ToList();
        var total = files.Sum(f => f.Length);
        result = result with { Files = files.Count, Bytes = total, OnlineOnly = onlineOnly };
        if (files.Count == 0) return Finish(result);

        Scratch.Sweep(plan.ScratchDirectory, started);
        Directory.CreateDirectory(plan.ScratchDirectory);
        var needed = Math.Max(Math.Min(plan.BatchBytes, total), files.Max(f => f.Length)) + SpareBytes;
        if (_freeSpace(plan.ScratchDirectory) is { } free && free < needed)
        {
            return Finish(result with
            {
                Refusal = Language.Pick(
                    $"The rehearsal needs {Humanize.Bytes(needed)} free where Boatdrill restores to ({plan.ScratchDirectory}), and there is {Humanize.Bytes(free)}.",
                    $"리허설을 하려면 Boatdrill이 복원하는 곳({plan.ScratchDirectory})에 빈 공간이 {Humanize.Bytes(needed)} 필요한데, 지금은 {Humanize.Bytes(free)}뿐입니다."),
            });
        }

        var scratch = Scratch.Create(plan.ScratchDirectory, Scratch.Prefix + "rehearsal-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            // An even spread of the folder's files, for the adapter to work out where its copies live.
            var stride = Math.Max(1, files.Count / MirrorFolderAdapter.ProbeFiles);
            var probeFiles = files.Where((_, i) => i % stride == 0).Take(MirrorFolderAdapter.ProbeFiles).ToList();
            var sample = new Sample(plan.Source, probeFiles, files.Count, total, 0, 0, null);
            var binding = await adapter.BindAsync(destination, sample, scratch.FullName, BindPurpose.ListOnly, [], ct).ConfigureAwait(false);

            // Without the backup's list of files, every file would read as having no copy; say why nothing was restored instead.
            if (binding.CouldNotLook)
            {
                return Finish(result with
                {
                    Refusal = Language.Pick($"Nothing was restored from {destination.Name}: {binding.Problem}.", $"{destination.Name}에서 아무것도 복원하지 않았습니다: {binding.Problem}."),
                });
            }

            var findings = new List<FileFinding>();
            long verified = 0, outdated = 0, corrupt = 0, missing = 0, placeholder = 0, errors = 0, bytesRestored = 0, filesDone = 0, bytesDone = 0;
            var restoreTime = TimeSpan.Zero;
            foreach (var batch in Batches(files, plan.BatchBytes))
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new RehearsalProgress(Language.Pick("Restoring from " + destination.Name, destination.Name + "에서 복원하는 중"), filesDone, files.Count, bytesDone, total));
                await adapter.PrefetchAsync(binding, batch.Select(f => f.RelativePath).ToList(), ct).ConfigureAwait(false);

                foreach (var file in batch)
                {
                    ct.ThrowIfCancellationRequested();
                    var (finding, read, elapsed, _, _) = await DrillRunner.VerifyFileAsync(adapter, binding, file, scratch.FullName, ct).ConfigureAwait(false);
                    bytesRestored += read;
                    restoreTime += elapsed;
                    switch (finding.Outcome)
                    {
                        case FileOutcome.Verified: verified++; break;
                        case FileOutcome.Outdated: outdated++; break;
                        case FileOutcome.Corrupt: corrupt++; break;
                        case FileOutcome.Missing: missing++; break;
                        case FileOutcome.Placeholder: placeholder++; break;
                        default: errors++; break;
                    }

                    // Keep the worst few hundred in memory, not every one of a folder that is missing entirely.
                    if (finding.Outcome != FileOutcome.Verified)
                    {
                        findings.Add(finding);
                        if (findings.Count >= 4 * MaxFindings) findings = WorstFirst(findings).Take(MaxFindings).ToList();
                    }

                    filesDone++;
                    bytesDone += file.Length;
                    progress?.Report(new RehearsalProgress(Language.Pick("Restoring from " + destination.Name, destination.Name + "에서 복원하는 중"), filesDone, files.Count, bytesDone, total));
                }
            }

            return Finish(result with
            {
                Verified = verified,
                Outdated = outdated,
                Corrupt = corrupt,
                Missing = missing,
                Placeholder = placeholder,
                Errors = errors,
                BytesRestored = bytesRestored,
                RestoreTime = restoreTime,
                Findings = WorstFirst(findings).Take(MaxFindings).ToList(),
            });
        }
        finally
        {
            scratch.Dispose();
        }
    }

    /// <summary>Why the plan's folder cannot be rehearsed, if it cannot: it leads out of the source, or it is not there.</summary>
    private static string? FolderProblem(RehearsalPlan plan)
    {
        if (string.IsNullOrEmpty(plan.Folder)) return null;

        var root = Path.GetFullPath(plan.Source.RootPath);
        var folder = Path.GetFullPath(Path.Combine(root, plan.Folder));
        if (!SourceFiles.IsInside(root, folder))
        {
            return Language.Pick(
                $"{plan.Folder} is not a folder inside {plan.Source.Name}, so nothing was restored.",
                $"{Korean.Topic(plan.Folder)} {plan.Source.Name} 안의 폴더가 아니어서 아무것도 복원하지 않았습니다.");
        }

        return Directory.Exists(folder)
            ? null
            : Language.Pick(
                $"There is no folder {plan.Folder} in {plan.Source.Name}, so nothing was restored.",
                $"{plan.Source.Name}에 {plan.Folder} 폴더가 없어 아무것도 복원하지 않았습니다.");
    }

    private RehearsalResult Finish(RehearsalResult result)
    {
        var finished = result with { FinishedUtc = _clock.GetUtcNow() };
        return finished with { Sentence = Sentence(finished) };
    }

    /// <summary>The rehearsal in one or two sentences, the answer first.</summary>
    public static string Sentence(RehearsalResult r)
    {
        if (Language.IsKorean) return KoreanSentence(r);

        var what = Label(r);
        if (!r.Reachable) return $"{r.DestinationName} could not be reached: {r.ProbeDetail}.";
        if (r.Refusal is { } refusal) return refusal;
        if (r.Files == 0) return r.OnlineOnly > 0 ? $"{what} has no files on this PC to restore. {OnlineOnlySentence(r.OnlineOnly)}" : $"{what} has no files to restore.";

        var lead = r.Verified == r.Files
            ? $"All {r.Files:N0} {(r.Files == 1 ? "file" : "files")} in {what} came back from {r.DestinationName} intact."
            : $"{r.Verified:N0} of {r.Files:N0} files in {what} came back from {r.DestinationName} intact";
        var facts = new List<string>();
        if (r.Missing > 0) facts.Add($"{r.Missing:N0} {(r.Missing == 1 ? "has" : "have")} no copy");
        if (r.Outdated > 0) facts.Add($"{r.Outdated:N0} {(r.Outdated == 1 ? "is" : "are")} older than the original");
        if (r.Corrupt > 0) facts.Add($"{r.Corrupt:N0} differ{(r.Corrupt == 1 ? "s" : "")} from the original without being older, which points to corruption");
        if (r.Placeholder > 0) facts.Add($"{r.Placeholder:N0} exist{(r.Placeholder == 1 ? "s" : "")} only in the cloud, not on this disk");
        if (r.Errors > 0) facts.Add($"{r.Errors:N0} could not be read");

        var text = facts.Count == 0 ? lead : $"{lead}; {string.Join(", ", facts.Take(facts.Count - 1))}{(facts.Count > 1 ? " and " : string.Empty)}{facts[^1]}.";
        if (r.BytesRestored > 0) text += $" Restoring {Humanize.Bytes(r.BytesRestored)} took {Humanize.Duration(r.RestoreTime)}.";
        return r.OnlineOnly > 0 ? text + " " + OnlineOnlySentence(r.OnlineOnly) : text;
    }

    /// <summary>Why some of the folder's files were not rehearsed, when some are online only.</summary>
    private static string OnlineOnlySentence(long count) => Language.IsKorean
        ? $"온라인 전용 파일 {count:N0}개는 내용이 이 PC에 없어 비교하지 않았습니다."
        : $"{count:N0} online-only {(count == 1 ? "file was" : "files were")} not compared: {(count == 1 ? "its" : "their")} bytes are not on this PC.";

    private static string KoreanSentence(RehearsalResult r)
    {
        var what = Label(r);
        if (!r.Reachable) return $"{r.DestinationName}에 연결할 수 없습니다: {r.ProbeDetail}.";
        if (r.Refusal is { } refusal) return refusal;
        if (r.Files == 0) return r.OnlineOnly > 0 ? $"{what}에는 이 PC에 있는 파일 중 복원할 것이 없습니다. {OnlineOnlySentence(r.OnlineOnly)}" : $"{what}에는 복원할 파일이 없습니다.";

        var sentences = new List<string>
        {
            r.Verified == r.Files
                ? $"{what}의 파일 {r.Files:N0}개가 모두 {r.DestinationName}에서 그대로 돌아왔습니다."
                : $"{what}의 파일 {r.Files:N0}개 중 {r.Verified:N0}개가 {r.DestinationName}에서 그대로 돌아왔습니다.",
        };
        if (r.Missing > 0) sentences.Add($"{r.Missing:N0}개는 사본이 없습니다.");
        if (r.Outdated > 0) sentences.Add($"{r.Outdated:N0}개는 원본보다 오래된 사본입니다.");
        if (r.Corrupt > 0) sentences.Add($"{r.Corrupt:N0}개는 원본보다 오래되지 않았는데도 내용이 달라 손상이 의심됩니다.");
        if (r.Placeholder > 0) sentences.Add($"{r.Placeholder:N0}개는 클라우드에만 있고 이 디스크에는 없습니다.");
        if (r.Errors > 0) sentences.Add($"{r.Errors:N0}개는 읽을 수 없었습니다.");
        if (r.BytesRestored > 0)
        {
            sentences.Add($"{Korean.Object(Humanize.Bytes(r.BytesRestored))} 복원하는 데 {Korean.Subject(Humanize.Duration(r.RestoreTime))} 걸렸습니다.");
        }

        if (r.OnlineOnly > 0) sentences.Add(OnlineOnlySentence(r.OnlineOnly));
        return string.Join(" ", sentences);
    }

    /// <summary>"Pictures", or "Pictures\2026" for a folder inside it.</summary>
    private static string Label(RehearsalResult r) =>
        string.IsNullOrEmpty(r.Folder) ? r.SourceName : r.SourceName + Path.DirectorySeparatorChar + r.Folder.Trim('/', '\\');

    private static IEnumerable<FileFinding> WorstFirst(IEnumerable<FileFinding> findings) =>
        findings
            .OrderBy(f => f.Outcome switch
            {
                FileOutcome.Corrupt => 0,
                FileOutcome.Error => 1,
                FileOutcome.Missing => 2,
                FileOutcome.Outdated => 3,
                _ => 4,
            })
            .ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);

    /// <summary>Files in folder order, cut into batches of at most <paramref name="batchBytes"/>; a larger file goes alone.</summary>
    private static IEnumerable<List<SampledFile>> Batches(IEnumerable<SampledFile> files, long batchBytes)
    {
        var batch = new List<SampledFile>();
        long bytes = 0;
        foreach (var file in files.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
        {
            if (batch.Count > 0 && bytes + file.Length > batchBytes)
            {
                yield return batch;
                batch = [];
                bytes = 0;
            }

            batch.Add(file);
            bytes += file.Length;
        }

        if (batch.Count > 0) yield return batch;
    }

    private static long? FreeSpace(string folder)
    {
        try
        {
            return new DriveInfo(Path.GetFullPath(folder)).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
