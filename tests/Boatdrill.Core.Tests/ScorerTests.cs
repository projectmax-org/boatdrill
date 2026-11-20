using Boatdrill.Core.Scoring;

namespace Boatdrill.Core.Tests;

public sealed class ScorerTests
{
    private static ScoreInputs Inputs(int sampled = 200, int verified = 200, int outdated = 0, int corrupt = 0, int missing = 0,
        TimeSpan? lag = null, bool reachable = true, double reachability = 1.0) =>
        new(sampled, verified, outdated, corrupt, missing, 0, 0, 10_000, lag, reachable, reachability);

    [Fact]
    public void PerfectInputsScore100()
    {
        var score = Scorer.Score(Inputs());

        Assert.Equal(100, score.Total);
        Assert.Equal(Verdict.WouldRestore, score.Verdict);
        Assert.Equal(0, score.CoverageMargin);
    }

    [Fact]
    public void CorruptionCapsTheTotal()
    {
        var score = Scorer.Score(Inputs(verified: 199, corrupt: 1));

        Assert.True(score.Total <= Scorer.CorruptionCap);
        Assert.Equal(Verdict.NeedsAttention, score.Verdict);
        Assert.Equal(0.995, score.Integrity, 3);
    }

    [Fact]
    public void UnreachableScoresZero()
    {
        var score = Scorer.Score(Inputs(reachable: false, reachability: 0.4));

        Assert.Equal(0, score.Total);
        Assert.Equal(Verdict.Unreachable, score.Verdict);
        Assert.Equal(0.4, score.Reachability);
    }

    [Fact]
    public void NothingSampledIsNothingToCheck()
    {
        Assert.Equal(Verdict.NothingToCheck, Scorer.Score(Inputs(sampled: 0, verified: 0)).Verdict);
    }

    [Fact]
    public void FreshnessFallsWithLag()
    {
        Assert.Equal(0.5, Scorer.Score(Inputs(lag: TimeSpan.FromDays(15))).Freshness, 3);
        Assert.Equal(0.0, Scorer.Score(Inputs(lag: TimeSpan.FromDays(45))).Freshness, 3);
        Assert.Equal(1.0, Scorer.Score(Inputs(lag: TimeSpan.Zero)).Freshness, 3);
    }

    [Fact]
    public void MissingFilesLowerCoverageIntoTheBands()
    {
        Assert.Equal(Verdict.WouldMostlyRestore, Scorer.Score(Inputs(verified: 150, missing: 50)).Verdict);
        Assert.Equal(Verdict.NeedsAttention, Scorer.Score(Inputs(verified: 100, missing: 100)).Verdict);
        Assert.Equal(Verdict.WouldNotRestore, Scorer.Score(Inputs(verified: 0, missing: 200)).Verdict);
    }

    [Fact]
    public void TheNumbersInScoringMd_AreTheScorersOwn()
    {
        // docs/SCORING.md quotes these: a change to the weights or the cap changes the page too.
        Assert.Equal(95, Scorer.Score(Inputs(lag: TimeSpan.FromDays(7))).Total);
        Assert.Equal(91, Scorer.Score(Inputs(lag: TimeSpan.FromDays(14))).Total);
        Assert.Equal(80, Scorer.Score(Inputs(lag: TimeSpan.FromDays(30))).Total);
        Assert.Equal(90, Scorer.Score(Inputs(verified: 180, missing: 20)).Total);
        Assert.Equal(50, Scorer.Score(Inputs(verified: 100, missing: 100)).Total);

        // The README's backup drive: all 400 found, 3 of them older than the original, a day behind.
        var drive = Scorer.Score(Inputs(sampled: 400, verified: 397, outdated: 3, lag: TimeSpan.FromDays(1)));
        Assert.Equal((99, Verdict.WouldRestore), (drive.Total, drive.Verdict));

        // All 200 of Documents found and none of the desktop's 10: 95, but a whole folder would be lost.
        ScoreInputs Folders(int withoutCopies) => new(210, 200, 0, 0, 10, 0, 0, 10_000, null, true, 1.0, SourcesWithoutCopies: withoutCopies);
        Assert.Equal(95, Scorer.Score(Folders(0)).Total);
        Assert.Equal((69, Verdict.NeedsAttention), (Scorer.Score(Folders(1)).Total, Scorer.Score(Folders(1)).Verdict));
    }

    [Fact]
    public void MarginShrinksWithTheSampleAndVanishesForACensus()
    {
        Assert.True(Scorer.Margin(0.95, 50, 10_000) > Scorer.Margin(0.95, 400, 10_000));
        Assert.Equal(0, Scorer.Margin(0.9, 100, 100));
        Assert.Equal(0.03, Scorer.Margin(0.95, 200, 10_000), 2);
    }
}
