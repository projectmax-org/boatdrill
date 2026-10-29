using Boatdrill.Core.Adapters;
using Boatdrill.Core.History;
using Boatdrill.Core.Sampling;
using Boatdrill.Core.Scoring;
using Boatdrill.Core.Text;
using Boatdrill.Core.Verification;

namespace Boatdrill.Core.Drill;

/// <summary>
/// Runs one drill: sample every source, then for every destination find each sampled file's copy, read it
/// back into the scratch folder, compare hashes and score the result. With a hash ledger, a few copies
/// verified in earlier drills are read again too, to catch bit rot. Destinations are visited one at a time
/// and files one at a time: disks, and above all USB drives and network shares, are fastest that way.
/// </summary>
public sealed class DrillRunner
{
    /// <summary>Files listed in a result, worst first. The counts always cover every sampled file.</summary>
    public const int MaxFindingsPerDestination = 500;

    /// <summary>FAT keeps write times to two seconds; a copy that close in time is the same version.</summary>
    private static readonly TimeSpan WriteTimeTolerance = TimeSpan.FromSeconds(2);

    private readonly IReadOnlyDictionary<DestinationKind, IDestinationAdapter> _adapters;
    private readonly TimeProvider _clock;
    private readonly Func<string, double?>? _reachabilityHistory;
    private readonly HashLedger? _ledger;

    /// <param name="adapters">One per destination kind; defaults to <see cref="DefaultAdapters"/>.</param>
    /// <param name="clock">Time source, replaceable in tests.</param>
    /// <param name="reachabilityHistory">Share of recent drills in which a destination (by id) was reachable, or null without history.</param>
    /// <param name="ledger">Copies verified before, re-checked and added to as the drill goes; the caller saves it afterwards.</param>
    public DrillRunner(IEnumerable<IDestinationAdapter>? adapters = null, TimeProvider? clock = null, Func<string, double?>? reachabilityHistory = null,
        HashLedger? ledger = null)
    {
        _adapters = (adapters ?? DefaultAdapters()).ToDictionary(a => a.Kind);
        _clock = clock ?? TimeProvider.System;
        _reachabilityHistory = reachabilityHistory;
        _ledger = ledger;
    }

    /// <summary>One adapter per kind of destination Boatdrill can read.</summary>
    public static IReadOnlyList<IDestinationAdapter> DefaultAdapters() => [new MirrorFolderAdapter(), new FileHistoryAdapter(), new ResticAdapter()];

    public async Task<DrillReport> RunAsync(DrillPlan plan, IProgress<DrillProgress>? progress = null, CancellationToken ct = default)
    {
        var started = _clock.GetUtcNow();
        var id = started.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
        Scratch.Sweep(plan.ScratchDirectory, started);
        var scratch = Directory.CreateDirectory(Path.Combine(plan.ScratchDirectory, Scratch.Prefix + id));
        try
        {
            var sampler = new Sampler(plan.Sampling, _clock);
            var samples = new List<Sample>();
            foreach (var source in plan.Sources)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new DrillProgress("Sampling " + source.Name, null, 0, 0, 0));
                samples.Add(sampler.Take(source));
            }

            var results = new List<DestinationResult>();
            foreach (var destination in plan.Destinations)
            {
                ct.ThrowIfCancellationRequested();
                results.Add(await VerifyDestinationAsync(destination, samples, scratch.FullName, progress, ct).ConfigureAwait(false));
            }

            return new DrillReport
            {
                Id = id,
                StartedUtc = started,
                FinishedUtc = _clock.GetUtcNow(),
                Trigger = plan.Trigger,
                Sources = samples.Select(Summarise).ToList(),
                Destinations = results,
            };
        }
        finally
        {
            // A file another program held open stays behind; a later drill's sweep removes it.
            Scratch.TryDelete(scratch.FullName);
        }
    }

    private static SourceSummary Summarise(Sample s) => new(
        s.Source.Id, s.Source.Name, s.Source.RootPath, s.Population, s.PopulationBytes,
        s.Files.Count, s.SampledBytes, s.SkippedLargeFiles, s.SkippedChurningFiles, s.NewestWriteUtc);

    private async Task<DestinationResult> VerifyDestinationAsync(
        Destination destination, List<Sample> samples, string scratch, IProgress<DrillProgress>? progress, CancellationToken ct)
    {
        if (!_adapters.TryGetValue(destination.Kind, out var adapter))
        {
            throw new NotSupportedException($"No adapter for {destination.Kind}.");
        }

        progress?.Report(new DrillProgress("Checking " + destination.Name, destination.Name, 0, 0, 0));
        var probe = await adapter.ProbeAsync(destination, ct).ConfigureAwait(false);
        var reachability = Reachability(_reachabilityHistory?.Invoke(destination.Id), probe.Reachable);
        var now = _clock.GetUtcNow();

        if (!probe.Reachable)
        {
            var unreachable = new DestinationResult
            {
                DestinationId = destination.Id,
                DestinationName = destination.Name,
                Kind = destination.Kind,
                RootPath = destination.DisplayLocation,
                Reachable = false,
                ProbeDetail = probe.Detail,
                Population = samples.Sum(s => s.Population),
                PopulationBytes = samples.Sum(s => s.PopulationBytes),
                Score = Scorer.Score(new ScoreInputs(0, 0, 0, 0, 0, 0, 0, 0, null, false, reachability)),
                Sentence = string.Empty,
            };
            return unreachable with { Sentence = Verdicts.Sentence(unreachable, now) };
        }

        // Copies verified in earlier drills, read again to see that they have not changed since.
        var due = _ledger?.Due(destination.Id, samples
            .Where(s => s.Files.Count > 0)
            .GroupBy(s => s.Source.Id)
            .ToDictionary(g => g.Key, g => (IReadOnlyCollection<string>)g.SelectMany(s => s.Files).Select(f => f.RelativePath).ToList()), now).ToList() ?? [];

        var findings = new List<FileFinding>();
        var notCopied = new List<DateTimeOffset>();
        var bindings = new List<BoundSource>();
        int sampled = 0, verified = 0, outdated = 0, corrupt = 0, missing = 0, placeholder = 0, errors = 0, rechecked = 0, rotted = 0;
        long bytesRead = 0, population = 0, populationBytes = 0;
        var readTime = TimeSpan.Zero;
        var total = samples.Sum(s => s.Files.Count) + due.Count;
        var done = 0;
        DateTimeOffset? newestVerified = null;

        foreach (var sample in samples)
        {
            population += sample.Population;
            populationBytes += sample.PopulationBytes;
            if (sample.Files.Count == 0) continue;

            var rechecks = due.Where(e => e.SourceId == sample.Source.Id).ToList();
            due.RemoveAll(e => e.SourceId == sample.Source.Id);

            // Binding may restore the whole sample at once (restic), so it already counts as reading back.
            progress?.Report(new DrillProgress("Reading back from " + destination.Name, destination.Name, done, total, bytesRead));
            var binding = await adapter.BindAsync(destination, sample, scratch, BindPurpose.Read, rechecks.Select(e => e.RelativePath).ToList(), ct)
                .ConfigureAwait(false);
            bindings.Add(new BoundSource(sample.Source.Id, sample.Source.Name, binding.Layout, binding.BasePath, binding.ProbeHits, binding.ProbeTried)
            {
                Problem = binding.Problem,
            });

            foreach (var file in sample.Files)
            {
                ct.ThrowIfCancellationRequested();
                sampled++;
                var (finding, read, elapsed, copy, hash) = await VerifyFileAsync(adapter, binding, file, scratch, ct).ConfigureAwait(false);
                bytesRead += read;
                readTime += elapsed;
                if (finding.Outcome == FileOutcome.Verified && copy?.StableIdentity is { } identity && hash is not null)
                {
                    _ledger?.Record(new LedgerEntry(destination.Id, sample.Source.Id, file.RelativePath, identity, copy.Length, copy.LastWriteUtc, hash,
                        file.Length, file.LastWriteUtc, now));
                }

                switch (finding.Outcome)
                {
                    case FileOutcome.Verified:
                        verified++;
                        if (newestVerified is null || file.LastWriteUtc > newestVerified) newestVerified = file.LastWriteUtc;
                        break;
                    case FileOutcome.Outdated: outdated++; break;
                    case FileOutcome.Corrupt: corrupt++; break;
                    case FileOutcome.Missing: missing++; break;
                    case FileOutcome.Placeholder: placeholder++; break;
                    default: errors++; break;
                }

                if (finding.Outcome is FileOutcome.Missing or FileOutcome.Outdated or FileOutcome.Placeholder) notCopied.Add(file.LastWriteUtc);
                if (finding.Outcome != FileOutcome.Verified) findings.Add(finding);
                done++;
                progress?.Report(new DrillProgress("Reading back from " + destination.Name, destination.Name, done, total, bytesRead));
            }

            foreach (var entry in rechecks)
            {
                ct.ThrowIfCancellationRequested();
                var (recheck, finding, read, elapsed) = await RecheckAsync(adapter, binding, entry, scratch, ct).ConfigureAwait(false);
                bytesRead += read;
                readTime += elapsed;
                if (finding is not null) findings.Add(finding);

                switch (recheck)
                {
                    case Recheck.Unchanged:
                        rechecked++;
                        _ledger!.Record(entry with { VerifiedUtc = now, RotSeenUtc = null });
                        break;
                    case Recheck.Rotted:
                        rechecked++;
                        rotted++;
                        _ledger!.Record(entry with { RotSeenUtc = entry.RotSeenUtc ?? now });
                        break;
                    case Recheck.Replaced:
                        _ledger!.Forget(entry);
                        break;
                }

                done++;
                progress?.Report(new DrillProgress("Reading back from " + destination.Name, destination.Name, done, total, bytesRead));
            }
        }

        // Freshness looks only at changes newer than anything verified: an old file that was never copied is a
        // coverage hole, not a lag. The oldest such change says how long the backup has been falling behind.
        var floor = newestVerified ?? DateTimeOffset.MinValue;
        var behindSince = notCopied.Where(t => t > floor).Select(t => (DateTimeOffset?)t).Min();
        TimeSpan? lag = behindSince is { } since ? (now - since < TimeSpan.Zero ? TimeSpan.Zero : now - since) : null;

        double? bytesPerSecond = readTime > TimeSpan.Zero && bytesRead > 0 ? bytesRead / readTime.TotalSeconds : null;
        TimeSpan? estimate = bytesPerSecond is > 0 ? TimeSpan.FromSeconds(populationBytes / bytesPerSecond.Value) : null;

        var score = Scorer.Score(new ScoreInputs(sampled, verified, outdated, corrupt, missing, placeholder, errors, population, lag, true, reachability,
            rechecked, rotted));
        var result = new DestinationResult
        {
            DestinationId = destination.Id,
            DestinationName = destination.Name,
            Kind = destination.Kind,
            RootPath = destination.DisplayLocation,
            Reachable = true,
            ProbeDetail = probe.Detail,
            FreeBytes = probe.FreeBytes,
            Bindings = bindings,
            Sampled = sampled,
            Verified = verified,
            Outdated = outdated,
            Corrupt = corrupt,
            Missing = missing,
            Placeholder = placeholder,
            Errors = errors,
            Rechecked = rechecked,
            Rotted = rotted,
            Population = population,
            PopulationBytes = populationBytes,
            BytesRead = bytesRead,
            ReadTime = readTime,
            BytesPerSecond = bytesPerSecond,
            EstimatedFullRestore = estimate,
            BehindSince = behindSince,
            Lag = lag,
            Score = score,
            Sentence = string.Empty,
            Findings = findings
                .OrderBy(f => f.Outcome switch
                {
                    FileOutcome.Rotted or FileOutcome.Corrupt => 0,
                    FileOutcome.Error => 1,
                    FileOutcome.Missing => 2,
                    FileOutcome.Outdated => 3,
                    _ => 4,
                })
                .ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
                .Take(MaxFindingsPerDestination)
                .ToList(),
        };
        return result with { Sentence = Verdicts.Sentence(result, now) };
    }

    /// <summary>Recent history counts for most of the reachability part; this drill for the rest. No history: this drill alone.</summary>
    internal static double Reachability(double? history, bool reachableNow)
    {
        var now = reachableNow ? 1.0 : 0.0;
        return history is null ? now : 0.7 * Math.Clamp(history.Value, 0, 1) + 0.3 * now;
    }

    private static async Task<(FileFinding Finding, long BytesRead, TimeSpan ReadTime, CopyLookup? Copy, string? CopyHash)> VerifyFileAsync(
        IDestinationAdapter adapter, SourceBinding binding, SampledFile file, string scratch, CancellationToken ct)
    {
        var copy = await adapter.LocateAsync(binding, file.RelativePath, ct).ConfigureAwait(false);
        switch (copy.State)
        {
            case CopyState.Missing:
                return (new FileFinding(file.RelativePath, FileOutcome.Missing, file.Length, file.LastWriteUtc, null, null,
                    copy.Detail ?? $"expected at {copy.Path}"), 0, TimeSpan.Zero, null, null);
            case CopyState.Placeholder:
                return (new FileFinding(file.RelativePath, FileOutcome.Placeholder, file.Length, file.LastWriteUtc, copy.Length, copy.LastWriteUtc, copy.Detail),
                    0, TimeSpan.Zero, null, null);
            case CopyState.Error:
                return (new FileFinding(file.RelativePath, FileOutcome.Error, file.Length, file.LastWriteUtc, null, null, copy.Detail), 0, TimeSpan.Zero, null, null);
        }

        var scratchFile = Path.Combine(scratch, Guid.NewGuid().ToString("N"));
        try
        {
            var readBack = await adapter.ReadBackAsync(binding, copy, scratchFile, ct).ConfigureAwait(false);
            if (!readBack.Success)
            {
                return (new FileFinding(file.RelativePath, readBack.Damaged ? FileOutcome.Corrupt : FileOutcome.Error, file.Length, file.LastWriteUtc,
                    copy.Length, copy.LastWriteUtc, readBack.Detail), readBack.BytesRead, readBack.Elapsed, copy, null);
            }

            string sourceHash;
            try
            {
                sourceHash = await Hashing.Sha256HexAsync(file.FullPath, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (new FileFinding(file.RelativePath, FileOutcome.Error, file.Length, file.LastWriteUtc, copy.Length, copy.LastWriteUtc,
                    "the original could not be read: " + ex.Message), readBack.BytesRead, readBack.Elapsed, copy, readBack.Sha256);
            }

            if (string.Equals(sourceHash, readBack.Sha256, StringComparison.Ordinal))
            {
                return (new FileFinding(file.RelativePath, FileOutcome.Verified, file.Length, file.LastWriteUtc, copy.Length, copy.LastWriteUtc, null),
                    readBack.BytesRead, readBack.Elapsed, copy, readBack.Sha256);
            }

            var copyOlder = copy.LastWriteUtc is { } written && written < file.LastWriteUtc - WriteTimeTolerance;
            var detail = copyOlder
                ? $"the copy was written {Humanize.Age(file.LastWriteUtc - copy.LastWriteUtc!.Value)} before the original"
                : copy.Length == file.Length
                    ? "same size and date as the original, different bytes"
                    : $"the copy is {copy.Length:N0} bytes, the original {file.Length:N0}, and the copy is not older";
            return (new FileFinding(file.RelativePath, copyOlder ? FileOutcome.Outdated : FileOutcome.Corrupt, file.Length, file.LastWriteUtc,
                copy.Length, copy.LastWriteUtc, detail), readBack.BytesRead, readBack.Elapsed, copy, readBack.Sha256);
        }
        finally
        {
            try
            {
                File.Delete(scratchFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Swept with the scratch folder when the drill ends.
            }
        }
    }

    private enum Recheck
    {
        /// <summary>Read again and still the same.</summary>
        Unchanged,

        /// <summary>Other bytes under the same name, size and date, or the backup tool reports the copy damaged.</summary>
        Rotted,

        /// <summary>The backup replaced or removed the copy, so there is nothing left to compare with.</summary>
        Replaced,

        /// <summary>Could not be checked this time; tried again in a later drill.</summary>
        Skipped,
    }

    /// <summary>Reads again a copy verified in an earlier drill and compares it with itself as it was then.</summary>
    private static async Task<(Recheck Result, FileFinding? Finding, long BytesRead, TimeSpan ReadTime)> RecheckAsync(
        IDestinationAdapter adapter, SourceBinding binding, LedgerEntry entry, string scratch, CancellationToken ct)
    {
        var copy = await adapter.LocateAsync(binding, entry.RelativePath, ct).ConfigureAwait(false);
        if (copy.State == CopyState.Missing)
        {
            // Gone while the source's other copies are still found: removed on purpose, most likely. When none
            // are found, the fault is more likely in finding them, so the entry is kept for another drill.
            return (binding.ProbeHits > 0 ? Recheck.Replaced : Recheck.Skipped, null, 0, TimeSpan.Zero);
        }

        if (copy.State != CopyState.Found) return (Recheck.Skipped, null, 0, TimeSpan.Zero);
        if (!SameCopy(entry, copy)) return (Recheck.Replaced, null, 0, TimeSpan.Zero);

        FileFinding Finding(FileOutcome outcome, string detail) =>
            new(entry.RelativePath, outcome, entry.SourceLength, entry.SourceWriteUtc, copy.Length, copy.LastWriteUtc, detail) { Recheck = true };

        var verifiedOn = $"verified on {entry.VerifiedUtc.ToLocalTime():d MMMM yyyy}";
        var scratchFile = Path.Combine(scratch, Guid.NewGuid().ToString("N"));
        try
        {
            var readBack = await adapter.ReadBackAsync(binding, copy, scratchFile, ct).ConfigureAwait(false);
            if (!readBack.Success)
            {
                return readBack.Damaged
                    ? (Recheck.Rotted, Finding(FileOutcome.Rotted, $"{verifiedOn}; now {readBack.Detail}"), readBack.BytesRead, readBack.Elapsed)
                    : (Recheck.Skipped, Finding(FileOutcome.Error, $"{verifiedOn}, it could not be read again: {readBack.Detail}"), readBack.BytesRead, readBack.Elapsed);
            }

            if (string.Equals(readBack.Sha256, entry.CopySha256, StringComparison.Ordinal))
            {
                return (Recheck.Unchanged, null, readBack.BytesRead, readBack.Elapsed);
            }

            // A tool that checks its own bytes would have refused damaged ones: these are a new version.
            if (adapter.ChecksItsOwnBytes) return (Recheck.Replaced, null, readBack.BytesRead, readBack.Elapsed);

            return (Recheck.Rotted, Finding(FileOutcome.Rotted, $"{verifiedOn}; its bytes have changed since, though its size and date have not"),
                readBack.BytesRead, readBack.Elapsed);
        }
        finally
        {
            Scratch.TryDeleteFile(scratchFile);
        }
    }

    /// <summary>Is this still the copy the ledger saw: the same name, size and write time?</summary>
    private static bool SameCopy(LedgerEntry entry, CopyLookup copy) =>
        string.Equals(entry.CopyIdentity, copy.StableIdentity, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
        entry.CopyLength == copy.Length &&
        (entry.CopyWriteUtc, copy.LastWriteUtc) switch
        {
            (null, null) => true,
            ({ } then, { } now) => (then - now).Duration() <= WriteTimeTolerance,
            _ => false,
        };
}
