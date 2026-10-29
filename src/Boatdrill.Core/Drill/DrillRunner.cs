using Boatdrill.Core.Adapters;
using Boatdrill.Core.Sampling;
using Boatdrill.Core.Scoring;
using Boatdrill.Core.Text;
using Boatdrill.Core.Verification;

namespace Boatdrill.Core.Drill;

/// <summary>
/// Runs one drill: sample every source, then for every destination find each sampled file's copy, read it
/// back into the scratch folder, compare hashes and score the result. Destinations are visited one at a
/// time and files one at a time: disks, and above all USB drives and network shares, are fastest that way.
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

    /// <param name="adapters">One per destination kind; defaults to <see cref="DefaultAdapters"/>.</param>
    /// <param name="clock">Time source, replaceable in tests.</param>
    /// <param name="reachabilityHistory">Share of recent drills in which a destination (by id) was reachable, or null without history.</param>
    public DrillRunner(IEnumerable<IDestinationAdapter>? adapters = null, TimeProvider? clock = null, Func<string, double?>? reachabilityHistory = null)
    {
        _adapters = (adapters ?? DefaultAdapters()).ToDictionary(a => a.Kind);
        _clock = clock ?? TimeProvider.System;
        _reachabilityHistory = reachabilityHistory;
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

        var findings = new List<FileFinding>();
        var notCopied = new List<DateTimeOffset>();
        var bindings = new List<BoundSource>();
        int sampled = 0, verified = 0, outdated = 0, corrupt = 0, missing = 0, placeholder = 0, errors = 0;
        long bytesRead = 0, population = 0, populationBytes = 0;
        var readTime = TimeSpan.Zero;
        var total = samples.Sum(s => s.Files.Count);
        var done = 0;
        DateTimeOffset? newestVerified = null;

        foreach (var sample in samples)
        {
            population += sample.Population;
            populationBytes += sample.PopulationBytes;
            if (sample.Files.Count == 0) continue;

            // Binding may restore the whole sample at once (restic), so it already counts as reading back.
            progress?.Report(new DrillProgress("Reading back from " + destination.Name, destination.Name, done, total, bytesRead));
            var binding = await adapter.BindAsync(destination, sample, scratch, BindPurpose.Read, ct).ConfigureAwait(false);
            bindings.Add(new BoundSource(sample.Source.Id, sample.Source.Name, binding.Layout, binding.BasePath, binding.ProbeHits, binding.ProbeTried)
            {
                Problem = binding.Problem,
            });

            foreach (var file in sample.Files)
            {
                ct.ThrowIfCancellationRequested();
                sampled++;
                var (finding, read, elapsed) = await VerifyFileAsync(adapter, binding, file, scratch, ct).ConfigureAwait(false);
                bytesRead += read;
                readTime += elapsed;

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
        }

        // Freshness looks only at changes newer than anything verified: an old file that was never copied is a
        // coverage hole, not a lag. The oldest such change says how long the backup has been falling behind.
        var floor = newestVerified ?? DateTimeOffset.MinValue;
        var behindSince = notCopied.Where(t => t > floor).Select(t => (DateTimeOffset?)t).Min();
        TimeSpan? lag = behindSince is { } since ? (now - since < TimeSpan.Zero ? TimeSpan.Zero : now - since) : null;

        double? bytesPerSecond = readTime > TimeSpan.Zero && bytesRead > 0 ? bytesRead / readTime.TotalSeconds : null;
        TimeSpan? estimate = bytesPerSecond is > 0 ? TimeSpan.FromSeconds(populationBytes / bytesPerSecond.Value) : null;

        var score = Scorer.Score(new ScoreInputs(sampled, verified, outdated, corrupt, missing, placeholder, errors, population, lag, true, reachability));
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
                    FileOutcome.Corrupt => 0,
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

    private static async Task<(FileFinding Finding, long BytesRead, TimeSpan ReadTime)> VerifyFileAsync(
        IDestinationAdapter adapter, SourceBinding binding, SampledFile file, string scratch, CancellationToken ct)
    {
        var copy = await adapter.LocateAsync(binding, file.RelativePath, ct).ConfigureAwait(false);
        switch (copy.State)
        {
            case CopyState.Missing:
                return (new FileFinding(file.RelativePath, FileOutcome.Missing, file.Length, file.LastWriteUtc, null, null,
                    copy.Detail ?? $"expected at {copy.Path}"), 0, TimeSpan.Zero);
            case CopyState.Placeholder:
                return (new FileFinding(file.RelativePath, FileOutcome.Placeholder, file.Length, file.LastWriteUtc, copy.Length, copy.LastWriteUtc, copy.Detail), 0, TimeSpan.Zero);
            case CopyState.Error:
                return (new FileFinding(file.RelativePath, FileOutcome.Error, file.Length, file.LastWriteUtc, null, null, copy.Detail), 0, TimeSpan.Zero);
        }

        var scratchFile = Path.Combine(scratch, Guid.NewGuid().ToString("N"));
        try
        {
            var readBack = await adapter.ReadBackAsync(binding, copy, scratchFile, ct).ConfigureAwait(false);
            if (!readBack.Success)
            {
                return (new FileFinding(file.RelativePath, readBack.Damaged ? FileOutcome.Corrupt : FileOutcome.Error, file.Length, file.LastWriteUtc,
                    copy.Length, copy.LastWriteUtc, readBack.Detail), readBack.BytesRead, readBack.Elapsed);
            }

            string sourceHash;
            try
            {
                sourceHash = await Hashing.Sha256HexAsync(file.FullPath, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (new FileFinding(file.RelativePath, FileOutcome.Error, file.Length, file.LastWriteUtc, copy.Length, copy.LastWriteUtc,
                    "the original could not be read: " + ex.Message), readBack.BytesRead, readBack.Elapsed);
            }

            if (string.Equals(sourceHash, readBack.Sha256, StringComparison.Ordinal))
            {
                return (new FileFinding(file.RelativePath, FileOutcome.Verified, file.Length, file.LastWriteUtc, copy.Length, copy.LastWriteUtc, null),
                    readBack.BytesRead, readBack.Elapsed);
            }

            var copyOlder = copy.LastWriteUtc is { } written && written < file.LastWriteUtc - WriteTimeTolerance;
            var detail = copyOlder
                ? $"the copy was written {Humanize.Age(file.LastWriteUtc - copy.LastWriteUtc!.Value)} before the original"
                : copy.Length == file.Length
                    ? "same size and date as the original, different bytes"
                    : $"the copy is {copy.Length:N0} bytes, the original {file.Length:N0}, and the copy is not older";
            return (new FileFinding(file.RelativePath, copyOlder ? FileOutcome.Outdated : FileOutcome.Corrupt, file.Length, file.LastWriteUtc,
                copy.Length, copy.LastWriteUtc, detail), readBack.BytesRead, readBack.Elapsed);
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
}
