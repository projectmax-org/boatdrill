namespace Boatdrill.Core.Scoring;

/// <summary>Ordered from best to worst, so the worst of several is the largest value.</summary>
public enum Verdict
{
    NothingToCheck,
    WouldRestore,
    WouldMostlyRestore,
    NeedsAttention,
    WouldNotRestore,
    Unreachable,

    /// <summary>
    /// An error the drill did not foresee stopped it checking the backup, so the drill says nothing of whether it
    /// would restore; the result's <see cref="Drill.DestinationResult.Failure"/> says why.
    /// </summary>
    CouldNotCheck,
}

/// <summary>The four parts of readiness, each 0 to 1, and the total 0 to 100. docs/SCORING.md explains the arithmetic.</summary>
public sealed record ReadinessScore(
    double Coverage,
    double Freshness,
    double Integrity,
    double Reachability,
    int Total,
    double CoverageMargin,
    Verdict Verdict);
