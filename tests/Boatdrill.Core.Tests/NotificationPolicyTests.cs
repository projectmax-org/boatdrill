using Boatdrill.Core.Drill;
using Boatdrill.Core.Notifications;
using Boatdrill.Core.Scoring;

namespace Boatdrill.Core.Tests;

public sealed class NotificationPolicyTests
{
    /// <param name="missing">Of 200 sampled files, how many have no copy; the rest read back correctly.</param>
    private static DestinationResult Result(string name, Verdict verdict, string sentence, string? probe = null, int missing = 0) => new()
    {
        DestinationId = name.ToLowerInvariant(),
        DestinationName = name,
        RootPath = "/mnt/" + name,
        Reachable = verdict != Verdict.Unreachable,
        ProbeDetail = probe,
        Sampled = 200,
        Verified = 200 - missing,
        Missing = missing,
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
            Result("NAS", Verdict.NeedsAttention, "NAS needs attention. 150 of 200 sampled files read back correctly and 50 have no copy.", missing: 50)));

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
    public void ADrillThatReadNothingBack_IsNotSilent_AndSaysWhy()
    {
        // Every file of the protected folders is online only or too large: each scheduled drill reads nothing back,
        // and silence about that would be taken for backups that would restore.
        var documents = new SourceSummary("d", "Documents", "/home/you/Documents", 3, 3000, 0, 0, SkippedLargeFiles: 3, 0, null, SkippedOnlineOnlyFiles: 12);
        var nothing = Result("Drive", Verdict.NothingToCheck, "unused") with { Sampled = 0, Verified = 0 };
        var report = Report(nothing, nothing with { DestinationId = "nas", DestinationName = "NAS" }) with { Sources = [documents] };

        var notice = NotificationPolicy.For(report);

        Assert.NotNull(notice);
        Assert.Equal("Nothing could be checked", notice.Title);
        Assert.Equal("No file could be read: 12 are online only and 3 are too large.", notice.Text);

        var empty = NotificationPolicy.For(report with { Sources = [documents with { Population = 0, PopulationBytes = 0, SkippedLargeFiles = 0, SkippedOnlineOnlyFiles = 0 }] });
        Assert.Equal("The protected folders hold no files to check.", empty!.Text);

        using (new InKorean())
        {
            var korean = NotificationPolicy.For(report);
            Assert.Equal("확인할 수 있는 파일이 없었습니다", korean!.Title);
            Assert.Equal("읽을 수 있는 파일이 없었습니다. 제외한 파일: 온라인 전용 파일 12개, 너무 큰 파일 3개.", korean.Text);
        }

        // A backup that could not be reached is still the thing to say first.
        var unplugged = NotificationPolicy.For(report with { Destinations = [nothing, Result("SSD", Verdict.Unreachable, "unused", probe: "E:\\ was not found")] });
        Assert.Equal("SSD could not be reached", unplugged!.Title);
    }

    [Fact]
    public void ADrillThatCouldNotFinish_SaysWhy_AndThatNothingWasChecked()
    {
        var notice = NotificationPolicy.ForFailedDrill("There is not enough space on the disk. ");

        Assert.Equal("The drill could not finish", notice.Title);
        Assert.Equal("There is not enough space on the disk. Your backups were not checked this time.", notice.Text);
        using (new InKorean())
        {
            var korean = NotificationPolicy.ForFailedDrill("There is not enough space on the disk.");
            Assert.Equal("복원 훈련을 끝내지 못했습니다", korean.Title);
            Assert.Equal("There is not enough space on the disk. 이번에는 백업을 확인하지 못했습니다.", korean.Text);
        }
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
