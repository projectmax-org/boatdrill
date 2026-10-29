using Boatdrill.Core.Drill;
using Boatdrill.Core.Notifications;
using Boatdrill.Core.Scoring;

namespace Boatdrill.Core.Tests;

public sealed class NotificationPolicyTests
{
    private static DestinationResult Result(string name, Verdict verdict, string sentence, string? probe = null) => new()
    {
        DestinationId = name.ToLowerInvariant(),
        DestinationName = name,
        RootPath = "/mnt/" + name,
        Reachable = verdict != Verdict.Unreachable,
        ProbeDetail = probe,
        Score = new ReadinessScore(1, 1, 1, 1, verdict == Verdict.WouldRestore ? 100 : 50, 0, verdict),
        Sentence = sentence,
    };

    private static DrillReport Report(params DestinationResult[] destinations) => new()
    {
        Id = "r1",
        StartedUtc = DateTimeOffset.UtcNow,
        FinishedUtc = DateTimeOffset.UtcNow,
        Trigger = DrillTrigger.Scheduled,
        Sources = [],
        Destinations = destinations,
    };

    [Fact]
    public void AllGood_StaysSilent()
    {
        Assert.Null(NotificationPolicy.For(Report(Result("Drive", Verdict.WouldRestore, "Drive would restore."))));
        Assert.Null(NotificationPolicy.For(Report(Result("Drive", Verdict.WouldMostlyRestore, "Drive would mostly restore."))));
    }

    [Fact]
    public void OneProblem_UsesTheVerdictWithoutRepeatingTheTitle()
    {
        var notice = NotificationPolicy.For(Report(
            Result("Drive", Verdict.WouldRestore, "Drive would restore."),
            Result("NAS", Verdict.NeedsAttention, "NAS needs attention. 150 of 200 sampled files read back correctly and 50 have no copy.")));

        Assert.NotNull(notice);
        Assert.Equal("NAS needs attention", notice.Title);
        Assert.Equal("150 of 200 sampled files read back correctly and 50 have no copy.", notice.Text);
    }

    [Fact]
    public void Unreachable_SaysWhereItLooked()
    {
        var notice = NotificationPolicy.For(Report(Result("Travel SSD", Verdict.Unreachable, "unused", probe: "E:\\ was not found")));

        Assert.NotNull(notice);
        Assert.Equal("Travel SSD could not be reached", notice.Title);
        Assert.StartsWith("E:\\ was not found. Plug it in", notice.Text);
    }

    [Fact]
    public void SeveralProblems_AreNamedTogether()
    {
        var notice = NotificationPolicy.For(Report(
            Result("NAS", Verdict.NeedsAttention, "NAS needs attention. x"),
            Result("SSD", Verdict.Unreachable, "unused", probe: "E:\\ was not found"),
            Result("Cloud", Verdict.WouldNotRestore, "Cloud would not restore. y")));

        Assert.NotNull(notice);
        Assert.Equal("3 backups need attention", notice.Title);
        Assert.Equal("SSD could not be reached, Cloud would not restore and NAS needs attention. Open Boatdrill for the details.", notice.Text);
    }

    [Fact]
    public void Shorten_CutsAtASentenceOrAWord()
    {
        var sentences = string.Join(" ", Enumerable.Repeat("This sentence is exactly forty-four chars.", 10));
        var cut = NotificationPolicy.Shorten(sentences, 100);
        Assert.True(cut.Length <= 100);
        Assert.EndsWith(".", cut);

        var words = string.Join(" ", Enumerable.Repeat("word", 60));
        var cutWords = NotificationPolicy.Shorten(words, 50);
        Assert.True(cutWords.Length <= 50);
        Assert.EndsWith("word…", cutWords);

        Assert.Equal("short", NotificationPolicy.Shorten("short", 50));
    }
}
