using System.Globalization;
using System.Runtime.CompilerServices;
using Boatdrill.Core.Drill;
using Boatdrill.Core.Notifications;
using Boatdrill.Core.Reporting;
using Boatdrill.Core.Sampling;
using Boatdrill.Core.Scoring;
using Boatdrill.Core.Text;
using Boatdrill.Core.Verification;

namespace Boatdrill.Core.Tests;

/// <summary>The tests read English, whatever language the machine running them speaks, unless a test asks for Korean.</summary>
internal static class TestLanguage
{
#pragma warning disable CA2255 // A test assembly: every test should start from the same language.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void SpeakEnglish()
    {
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
    }
}

/// <summary>Korean for the test that creates it (it flows with the test's awaits), English again after.</summary>
internal sealed class InKorean : IDisposable
{
    private readonly CultureInfo _before = CultureInfo.CurrentUICulture;

    public InKorean() => CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ko-KR");

    public void Dispose() => CultureInfo.CurrentUICulture = _before;
}

public sealed class KoreanTests
{
    private static readonly DateTimeOffset Finished = new(2026, 10, 27, 4, 57, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("사진", "사진은", "사진이", "사진을", "사진으로")]
    [InlineData("문서", "문서는", "문서가", "문서를", "문서로")]
    [InlineData("파일", "파일은", "파일이", "파일을", "파일로")]
    [InlineData("NAS", "NAS는", "NAS가", "NAS를", "NAS로")]
    [InlineData("Backup drive (E:)", "Backup drive (E:)는", "Backup drive (E:)가", "Backup drive (E:)를", "Backup drive (E:)로")]
    [InlineData("Google", "Google은", "Google이", "Google을", "Google로")]
    [InlineData("Laptop", "Laptop은", "Laptop이", "Laptop을", "Laptop으로")]
    [InlineData("Team", "Team은", "Team이", "Team을", "Team으로")]
    [InlineData("2026", "2026은", "2026이", "2026을", "2026으로")]
    [InlineData("2", "2는", "2가", "2를", "2로")]
    [InlineData("1", "1은", "1이", "1을", "1로")]
    [InlineData("10", "10은", "10이", "10을", "10으로")]
    [InlineData("26분", "26분은", "26분이", "26분을", "26분으로")]
    [InlineData("45초", "45초는", "45초가", "45초를", "45초로")]
    public void Particles_FollowTheSoundBefore(string word, string topic, string subject, string obj, string toward)
    {
        Assert.Equal(topic, Korean.Topic(word));
        Assert.Equal(subject, Korean.Subject(word));
        Assert.Equal(obj, Korean.Object(word));
        Assert.Equal(toward, Korean.Toward(word));
    }

    [Fact]
    public void Numbers_AreSaidInKorean()
    {
        using var korean = new InKorean();

        Assert.Equal("45초", Humanize.Duration(TimeSpan.FromSeconds(45)));
        Assert.Equal("12분", Humanize.Duration(TimeSpan.FromMinutes(12)));
        Assert.Equal("2시간 10분", Humanize.Duration(TimeSpan.FromMinutes(130)));
        Assert.Equal("3일", Humanize.Duration(TimeSpan.FromDays(3)));
        Assert.Equal("방금", Humanize.Ago(TimeSpan.FromSeconds(30)));
        Assert.Equal("5분 전", Humanize.Ago(TimeSpan.FromMinutes(5)));
        Assert.Equal("2개월 전", Humanize.Ago(TimeSpan.FromDays(61)));
        Assert.Equal("3일 후", Humanize.Until(TimeSpan.FromDays(3)));
        Assert.Equal("500바이트", Humanize.Bytes(500));
        Assert.Equal("1.5 GB", Humanize.Bytes(1536L * 1024 * 1024));
        Assert.Equal("파일 3개", Humanize.Count(3, "file", "파일"));
        Assert.Equal("2026년 10월 27일", Language.Date(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void English_StaysAsItWas()
    {
        Assert.False(Language.IsKorean);
        Assert.Equal("3 files", Humanize.Count(3, "file", "파일"));
        Assert.Equal("5 minutes ago", Humanize.Ago(TimeSpan.FromMinutes(5)));
        Assert.Equal("in 3 days", Humanize.Until(TimeSpan.FromDays(3)));
    }

    [Fact]
    public void AVerdict_ReadsInKorean_EvenWhenItWasStoredInEnglish()
    {
        var result = Result(verified: 396, outdated: 4) with
        {
            Sentence = "Weekly zips would restore. 396 of 400 sampled files read back correctly and 4 are older than the original.",
            BehindSince = Finished.AddDays(-4),
            EstimatedFullRestore = TimeSpan.FromMinutes(26),
            PopulationBytes = 212L * 1024 * 1024 * 1024,
        };

        using var korean = new InKorean();

        Assert.Equal(
            "Weekly zips에서 복원할 수 있습니다. 표본 파일 400개 중 396개를 제대로 읽어 왔습니다. 4개는 원본보다 오래된 사본입니다. " +
            $"아직 여기에 복사되지 않은 변경 가운데 가장 오래된 것은 {Language.Date(Finished.AddDays(-4))}(4일 전)의 것입니다. " +
            "측정한 속도라면 212 GB 전체를 복원하는 데 약 26분이 걸립니다.",
            Verdicts.Sentence(result, Finished));
        Assert.Equal("백업에서 복원할 수 있습니다", Verdicts.Headline([result]));
        Assert.Equal("복원 가능", Verdicts.Words(result.Score.Verdict));
        Assert.Equal("이전 버전", Verdicts.Words(FileOutcome.Outdated));
        Assert.Equal(["포함률", "최신성", "무결성", "연결률"], ScoreParts.Describe(result).Select(p => p.Name));
        Assert.Equal("D:\\Weekly zips의 압축 파일", Destination.Describe(DestinationKind.Archive, "D:\\Weekly zips"));
    }

    [Fact]
    public void Problems_AreNamedInKorean()
    {
        var nas = Result(verified: 150, missing: 50, verdict: Verdict.NeedsAttention, name: "가족 NAS");
        var drive = Result(verified: 0, verdict: Verdict.Unreachable, name: "외장 드라이브") with { Reachable = false, ProbeDetail = "E:\\를 찾을 수 없습니다" };
        var report = new DrillReport
        {
            Id = "r1",
            StartedUtc = Finished.AddMinutes(-4),
            FinishedUtc = Finished,
            Trigger = DrillTrigger.Scheduled,
            Sources = [],
            Destinations = [nas, drive],
        };

        using var korean = new InKorean();

        Assert.Equal("확인이 필요한 백업이 2개 있습니다", Verdicts.Headline(report.Destinations));
        var notice = NotificationPolicy.For(report);
        Assert.NotNull(notice);
        Assert.Equal("확인이 필요한 백업이 2개 있습니다", notice.Title);
        Assert.Equal("외장 드라이브에 연결할 수 없습니다. 가족 NAS에 확인할 문제가 있습니다. 자세한 내용은 Boatdrill에서 확인하세요.", notice.Text);

        var one = NotificationPolicy.For(report with { Destinations = [nas] });
        Assert.Equal("가족 NAS에 확인할 문제가 있습니다", one!.Title);
        Assert.Equal("표본 파일 200개 중 150개를 제대로 읽어 왔습니다. 50개는 사본이 없습니다.", one.Text);
    }

    [Fact]
    public async Task ADrill_InKorean_WritesItsDetailsAndReportInKorean()
    {
        using var source = new TempTree();
        using var backup = new TempTree();
        using var scratch = new TempTree();
        var lastWeek = DateTimeOffset.UtcNow.AddDays(-7);
        for (var i = 0; i < 4; i++) source.Write($"문서/{i}.txt", $"문서 {i}", lastWeek);
        source.MirrorTo(backup.Root, skip: ["문서/3.txt"]);

        using var korean = new InKorean();
        var report = await new DrillRunner().RunAsync(new DrillPlan
        {
            Sources = [Source.FromPath(source.Root, "문서")],
            Destinations = [Destination.MirrorFolder(backup.Root, "백업 드라이브"), Destination.MirrorFolder(backup.At("없는 폴더"), "외장 드라이브")],
            ScratchDirectory = scratch.Root,
            Sampling = new SampleOptions { SampleSize = 100, Seed = 1, ChurnWindow = TimeSpan.FromHours(-1), PerFolderFloor = 0 },
        });

        var found = report.Destinations[0];
        Assert.Equal((3, 1), (found.Verified, found.Missing));
        Assert.Contains("에 있어야 합니다", Assert.Single(found.Findings).Detail);
        Assert.StartsWith("백업 드라이브에서", found.Sentence);
        Assert.EndsWith("찾을 수 없습니다", report.Destinations[1].ProbeDetail);

        var html = HtmlReport.Render(report);
        Assert.Contains("<html lang=\"ko\">", html);
        Assert.Contains("보호하는 폴더", html);
        Assert.Contains("사본 없음", html);
    }

    private static DestinationResult Result(int verified, int missing = 0, int outdated = 0, Verdict verdict = Verdict.WouldRestore, string name = "Weekly zips")
    {
        var sampled = verified + missing + outdated;
        return new DestinationResult
        {
            DestinationId = "zips",
            DestinationName = name,
            Kind = DestinationKind.Archive,
            RootPath = "D:\\Weekly zips",
            Reachable = true,
            Sampled = sampled,
            Verified = verified,
            Missing = missing,
            Outdated = outdated,
            Score = new ReadinessScore(1, 0.87, 1, 1, verdict == Verdict.WouldRestore ? 97 : 50, 0, verdict),
            Sentence = string.Empty,
        };
    }
}
