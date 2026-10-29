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

    public long BytesRestored { get; init; }

    /// <summary>Time spent restoring and reading copies back: what a real restore of this folder would take.</summary>
    public TimeSpan RestoreTime { get; init; }

    /// <summary>The files that did not come back intact, worst first, at most <see cref="RehearsalRunner.MaxFindings"/>.</summary>
    public IReadOnlyList<FileFinding> Findings { get; init; } = [];

    public required string Sentence { get; init; }

    public bool AllIntact => Reachable && Refusal is null && Verified == Files;
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

        progress?.Report(new RehearsalProgress("Checking " + destination.Name, 0, 0, 0, 0));
        var probe = await adapter.ProbeAsync(destination, ct).ConfigureAwait(false);
        if (!probe.Reachable) return Finish(result with { ProbeDetail = probe.Detail });

        result = result with { Reachable = true, ProbeDetail = probe.Detail };
        progress?.Report(new RehearsalProgress("Listing " + Label(result), 0, 0, 0, 0));
        var files = SourceFiles.Enumerate(plan.Source, plan.Folder).ToList();
        var total = files.Sum(f => f.Length);
        result = result with { Files = files.Count, Bytes = total };
        if (files.Count == 0) return Finish(result);

        Scratch.Sweep(plan.ScratchDirectory, started);
        Directory.CreateDirectory(plan.ScratchDirectory);
        var needed = Math.Max(Math.Min(plan.BatchBytes, total), files.Max(f => f.Length)) + SpareBytes;
        if (_freeSpace(plan.ScratchDirectory) is { } free && free < needed)
        {
            return Finish(result with
            {
                Refusal = $"The rehearsal needs {Humanize.Bytes(needed)} free where Boatdrill restores to ({plan.ScratchDirectory}), and there is {Humanize.Bytes(free)}.",
            });
        }

        var scratch = Directory.CreateDirectory(Path.Combine(plan.ScratchDirectory, Scratch.Prefix + "rehearsal-" + Guid.NewGuid().ToString("N")[..8]));
        try
        {
            // An even spread of the folder's files, for the adapter to work out where its copies live.
            var stride = Math.Max(1, files.Count / MirrorFolderAdapter.ProbeFiles);
            var probeFiles = files.Where((_, i) => i % stride == 0).Take(MirrorFolderAdapter.ProbeFiles).ToList();
            var sample = new Sample(plan.Source, probeFiles, files.Count, total, 0, 0, null);
            var binding = await adapter.BindAsync(destination, sample, scratch.FullName, BindPurpose.ListOnly, [], ct).ConfigureAwait(false);

            var findings = new List<FileFinding>();
            long verified = 0, outdated = 0, corrupt = 0, missing = 0, placeholder = 0, errors = 0, bytesRestored = 0, filesDone = 0, bytesDone = 0;
            var restoreTime = TimeSpan.Zero;
            foreach (var batch in Batches(files, plan.BatchBytes))
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new RehearsalProgress("Restoring from " + destination.Name, filesDone, files.Count, bytesDone, total));
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
                    progress?.Report(new RehearsalProgress("Restoring from " + destination.Name, filesDone, files.Count, bytesDone, total));
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
            Scratch.TryDelete(scratch.FullName);
        }
    }

    private RehearsalResult Finish(RehearsalResult result)
    {
        var finished = result with { FinishedUtc = _clock.GetUtcNow() };
        return finished with { Sentence = Sentence(finished) };
    }

    /// <summary>The rehearsal in one or two sentences, the answer first.</summary>
    public static string Sentence(RehearsalResult r)
    {
        var what = Label(r);
        if (!r.Reachable) return $"{r.DestinationName} could not be reached: {r.ProbeDetail}.";
        if (r.Refusal is { } refusal) return refusal;
        if (r.Files == 0) return $"{what} has no files to restore.";

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
        return r.BytesRestored > 0
            ? $"{text} Restoring {Humanize.Bytes(r.BytesRestored)} took {Humanize.Duration(r.RestoreTime)}."
            : text;
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
