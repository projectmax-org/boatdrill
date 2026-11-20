using System.Text.Json.Serialization;
using Boatdrill.Core.Scoring;

namespace Boatdrill.Core.Drill;

/// <param name="Missing">The folder was not there when the drill ran, so nothing of it was checked. False in drills recorded before 1.0.</param>
/// <param name="SkippedOnlineOnlyFiles">Files left out because their bytes are in the cloud, not on this disk. Zero before 1.0.</param>
public sealed record SourceSummary(
    string SourceId,
    string Name,
    string RootPath,
    long Population,
    long PopulationBytes,
    int Sampled,
    long SampledBytes,
    long SkippedLargeFiles,
    long SkippedChurningFiles,
    DateTimeOffset? NewestWriteUtc,
    bool Missing = false,
    long SkippedOnlineOnlyFiles = 0);

/// <summary>One drill, start to finish. Appended to the history as one line of JSON.</summary>
public sealed record DrillReport
{
    public required string Id { get; init; }

    public required DateTimeOffset StartedUtc { get; init; }

    public required DateTimeOffset FinishedUtc { get; init; }

    public required DrillTrigger Trigger { get; init; }

    public required IReadOnlyList<SourceSummary> Sources { get; init; }

    public required IReadOnlyList<DestinationResult> Destinations { get; init; }

    public TimeSpan Duration => FinishedUtc - StartedUtc;

    /// <summary>The worst destination's verdict, which is what the tray icon shows.</summary>
    public Verdict Worst => Destinations.Count == 0 ? Verdict.NothingToCheck : Destinations.Max(d => d.Score.Verdict);

    /// <summary>
    /// The protected folders that were not there when the drill ran. No backup's score moves for them, since no
    /// backup is at fault, but none of their files were checked: a drill with any of these has not said the
    /// backups would restore everything the user asked about.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<SourceSummary> MissingSources => [.. Sources.Where(s => s.Missing)];
}
