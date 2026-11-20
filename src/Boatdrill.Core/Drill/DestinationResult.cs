using Boatdrill.Core.Scoring;
using Boatdrill.Core.Verification;

namespace Boatdrill.Core.Drill;

/// <summary>Where a source's copies were looked for inside a destination.</summary>
public sealed record BoundSource(string SourceId, string SourceName, MirrorLayout Layout, string BasePath, int ProbeHits, int ProbeTried)
{
    /// <summary>Why nothing could be found there, when the adapter knew.</summary>
    public string? Problem { get; init; }

    /// <summary>What the verdict says although copies were found: a newer archive that could not be opened, say.</summary>
    public string? Warning { get; init; }
}

/// <summary>One destination's side of a drill: the counts, the score, and the sentence the app shows.</summary>
public sealed record DestinationResult
{
    public required string DestinationId { get; init; }

    public required string DestinationName { get; init; }

    public DestinationKind Kind { get; init; } = DestinationKind.MirrorFolder;

    /// <summary>Where the destination is, as shown; a password in a repository URL is masked.</summary>
    public required string RootPath { get; init; }

    public required bool Reachable { get; init; }

    public string? ProbeDetail { get; init; }

    public long? FreeBytes { get; init; }

    public IReadOnlyList<BoundSource> Bindings { get; init; } = [];

    public int Sampled { get; init; }

    public int Verified { get; init; }

    public int Outdated { get; init; }

    public int Corrupt { get; init; }

    public int Missing { get; init; }

    public int Placeholder { get; init; }

    /// <summary>The newest canary stamp in the destination's copies (the oldest of them, with several sources): it last caught up after this.</summary>
    public DateTimeOffset? CanaryCopiedUtc { get; init; }

    /// <summary>The oldest canary stamp the destination's copies lack, past the grace period.</summary>
    public DateTimeOffset? CanaryBehindSince { get; init; }

    /// <summary>Copies verified in earlier drills that were read again in this one, from the hash ledger.</summary>
    public int Rechecked { get; init; }

    /// <summary>Of those, how many hold other bytes under the same name, size and date.</summary>
    public int Rotted { get; init; }

    public int Errors { get; init; }

    /// <summary>Files in all sources together, sampled or not.</summary>
    public long Population { get; init; }

    public long PopulationBytes { get; init; }

    public long BytesRead { get; init; }

    public TimeSpan ReadTime { get; init; }

    public double? BytesPerSecond { get; init; }

    /// <summary>How long restoring every file would take at the speed this drill measured.</summary>
    public TimeSpan? EstimatedFullRestore { get; init; }

    /// <summary>The write time of the oldest recent change that has no up-to-date copy here. Null when the backup is caught up.</summary>
    public DateTimeOffset? BehindSince { get; init; }

    public TimeSpan? Lag { get; init; }

    public required ReadinessScore Score { get; init; }

    public required string Sentence { get; init; }

    /// <summary>Every sampled file that was not verified, for the details view.</summary>
    public IReadOnlyList<FileFinding> Findings { get; init; } = [];
}
