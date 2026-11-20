namespace Boatdrill.Core.Sampling;

/// <summary>The files a drill will verify for one source, and what the whole source looks like.</summary>
/// <param name="Missing">
/// The source's folder was not there to sample: renamed, moved, or on a drive that is not connected. That is not
/// an empty folder, and a drill that found nothing to check there has to say so.
/// </param>
public sealed record Sample(
    Source Source,
    IReadOnlyList<SampledFile> Files,
    long Population,
    long PopulationBytes,
    long SkippedLargeFiles,
    long SkippedChurningFiles,
    DateTimeOffset? NewestWriteUtc,
    bool Missing = false)
{
    public long SampledBytes => Files.Sum(f => f.Length);

    /// <summary>Files left out because their bytes are in the cloud, not on this disk: reading one would download it.</summary>
    public long SkippedOnlineOnlyFiles { get; init; }
}
