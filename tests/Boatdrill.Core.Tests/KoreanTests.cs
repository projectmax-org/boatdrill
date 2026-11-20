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
    [InlineData("백업", "백업은", "백업이", "백업을", "백업으로")]
    [InlineData("Backup", "Backup은", "Backup이", "Backup을", "Backup으로")]
    [InlineData("Backups", "Backups는", "Backups가", "Backups를", "Backups로")]
    [InlineData("새 폴더", "새 폴더는", "새 폴더가", "새 폴더를", "새 폴더로")]
    [InlineData("백업 (E:)", "백업 (E:)은", "백업 (E:)이", "백업 (E:)을", "백업 (E:)으로")]
    [InlineData("새 볼륨 (D:)", "새 볼륨 (D:)은", "새 볼륨 (D:)이", "새 볼륨 (D:)을", "새 볼륨 (D:)으로")]
    [InlineData("파일 히스토리 (N:)", "파일 히스토리 (N:)는", "파일 히스토리 (N:)가", "파일 히스토리 (N:)를", "파일 히스토리 (N:)로")]
    [InlineData("외장하드 (M:)", "외장하드 (M:)는", "외장하드 (M:)가", "외장하드 (M:)를", "외장하드 (M:)로")]
    [InlineData(@"사진(C:\Users\you\Pictures)", @"사진(C:\Users\you\Pictures)은", @"사진(C:\Users\you\Pictures)이", @"사진(C:\Users\you\Pictures)을", @"사진(C:\Users\you\Pictures)으로")]
    [InlineData("(E:)", "(E:)는", "(E:)가", "(E:)를", "(E:)로")]
    [InlineData("restic", "restic은", "restic이", "restic을", "restic으로")]
    [InlineData("rclone", "rclone은", "rclone이", "rclone을", "rclone으로")]
    [InlineData("kopia", "kopia는", "kopia가", "kopia를", "kopia로")]
    [InlineData(@"C:\Users\me\Music", @"C:\Users\me\Music은", @"C:\Users\me\Music이", @"C:\Users\me\Music을", @"C:\Users\me\Music으로")]
    [InlineData("Public", "Public은", "Public이", "Public을", "Public으로")]
    [InlineData("Mac", "Mac은", "Mac이", "Mac을", "Mac으로")]
    [InlineData("home", "home은", "home이", "home을", "home으로")]
    [InlineData("Phone", "Phone은", "Phone이", "Phone을", "Phone으로")]
    [InlineData("Disc", "Disc는", "Disc가", "Disc를", "Disc로")]
    [InlineData("Sync", "Sync는", "Sync가", "Sync를", "Sync로")]
    [InlineData("Drive", "Drive는", "Drive가", "Drive를", "Drive로")]
    [InlineData("Acme", "Acme는", "Acme가", "Acme를", "Acme로")]
    public void Particles_FollowTheSoundBefore(string word, string topic, string subject, string obj, string toward)
    {
        Assert.Equal(topic, Korean.Topic(word));
        Assert.Equal(subject, Korean.Subject(word));
        Assert.Equal(obj, Korean.Object(word));
        Assert.Equal(toward, Korean.Toward(word));
    }

    [Theory]
    [InlineData("사진", "사진 폴더")]
    [InlineData("Projects", "Projects 폴더")]
    [InlineData("새 폴더", "새 폴더")]
    [InlineData("New folder", "New folder")]
    public void AFolder_IsCalledAFolderOnlyOnce(string name, string named) => Assert.Equal(named, Korean.Folder(name));

    [Theory]
    [InlineData("", "")]
    [InlineData("Backup drive (E:) would restore.", "Backup drive (E:) would restore.")]
    [InlineData("사", "사")]
    [InlineData("사진", "사\u2060진")]
    [InlineData("사진 2개", "사\u2060진 2\u2060개")]
    [InlineData("E:\\Backup에서 복원할 수 있습니다.", "E:\\Backup\u2060에\u2060서 복\u2060원\u2060할 수 있\u2060습\u2060니\u2060다.")]
    [InlineData("가족 NAS에 확인할", "가\u2060족 NAS\u2060에 확\u2060인\u2060할")]
    [InlineData("백업 드라이브 (E:)에서", "백\u2060업 드\u2060라\u2060이\u2060브 (E:)\u2060에\u2060서")]
    [InlineData("2026년 10월 26일, 문서·사진", "2026\u2060년 10\u2060월 26\u2060일, 문\u2060서·사\u2060진")]
    public void KoreanWords_AreKeptWhole_AndComeBackAsWritten(string written, string joined)
    {
        Assert.Equal(joined, Korean.KeepWordsWhole(written));
        Assert.Equal(written, Korean.WithoutJoiners(Korean.KeepWordsWhole(written)));
        Assert.Equal(joined, Korean.KeepWordsWhole(Korean.WithoutJoiners(joined)));
        Assert.Same(written, Korean.WithoutJoiners(written));
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
            "Weekly zips에서 복원할 수 있습니다. 표본 파일 400개 중 396개가 원본과 일치했습니다. 4개는 원본보다 오래된 사본입니다. " +
            $"아직 여기에 복사되지 않은 가장 오래된 변경 사항은 {Language.Date(Finished.AddDays(-4))}(4일 전)에 생겼습니다. " +
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
        Assert.Equal("표본 파일 200개 중 150개가 원본과 일치했습니다. 50개는 사본이 없습니다.", one.Text);
    }

    [Fact]
    public void TheFirstFact_SaysHowManyCopiesMatchTheOriginal_NoneAndOneIncluded()
    {
        using var korean = new InKorean();

        Assert.Equal("표본 파일 400개가 모두 원본과 일치했습니다.", Verdicts.Body(Result(verified: 400), Finished));
        Assert.Equal("표본 파일 1개가 원본과 일치했습니다.", Verdicts.Body(Result(verified: 1), Finished));
        Assert.Equal(
            "표본 파일 400개 중 원본과 일치한 파일이 없습니다. 400개는 사본이 없습니다.",
            Verdicts.Body(Result(verified: 0, missing: 400, verdict: Verdict.WouldNotRestore), Finished));
        Assert.Equal(
            "표본 파일 1개가 원본과 일치하지 않았습니다. 1개는 사본이 없습니다.",
            Verdicts.Body(Result(verified: 0, missing: 1, verdict: Verdict.WouldNotRestore), Finished));
    }

    [Fact]
    public void ABackupBehindTheCanary_SaysHowFarItsCopyGoes()
    {
        var copied = Finished.AddDays(-3);
        var result = Result(verified: 200) with { CanaryCopiedUtc = copied, CanaryBehindSince = Finished.AddDays(-2) };

        using var korean = new InKorean();

        Assert.EndsWith(
            $" 카나리아 기록은 {Language.DateAndTime(copied)}에 남긴 것까지만 여기에 있고, 그 뒤의 기록은 아직 복사되지 않았습니다.",
            Verdicts.Body(result, Finished));
    }

    [Fact]
    public void ARehearsal_SaysWhatWasRestoredIntact()
    {
        var all = Rehearsed(files: 400, verified: 400);
        var most = Rehearsed(files: 400, verified: 397) with { Missing = 2, Outdated = 1 };
        var one = Rehearsed(files: 1, verified: 1);
        var none = Rehearsed(files: 5, verified: 0) with { Missing = 5 };

        using var korean = new InKorean();

        Assert.Equal("백업 드라이브에서 사진의 파일 400개를 모두 온전히 복원했습니다.", RehearsalRunner.Sentence(all));
        Assert.Equal(
            "백업 드라이브에서 사진의 파일 400개 중 397개를 온전히 복원했습니다. 2개는 사본이 없습니다. 1개는 원본보다 오래된 사본입니다.",
            RehearsalRunner.Sentence(most));
        Assert.Equal("백업 드라이브에서 사진의 파일 1개를 온전히 복원했습니다.", RehearsalRunner.Sentence(one));
        Assert.Equal("백업 드라이브에서 사진의 파일 5개 중 온전히 복원한 파일이 없습니다. 5개는 사본이 없습니다.", RehearsalRunner.Sentence(none));
    }

    [Fact]
    public void AFolderThatIsGone_IsNamedInKorean()
    {
        var pictures = new SourceSummary("p", "사진", @"C:\Users\you\Pictures", 0, 0, 0, 0, 0, 0, null, Missing: true);
        var videos = new SourceSummary("v", "동영상", @"D:\동영상", 0, 0, 0, 0, 0, 0, null, Missing: true);
        var unnamed = new SourceSummary("n", "새 폴더", @"D:\새 폴더", 0, 0, 0, 0, 0, 0, null, Missing: true);
        var report = new DrillReport
        {
            Id = "r1",
            StartedUtc = Finished.AddMinutes(-4),
            FinishedUtc = Finished,
            Trigger = DrillTrigger.Scheduled,
            Sources = [pictures],
            Destinations = [Result(verified: 200)],
        };

        using var korean = new InKorean();

        Assert.Equal(@"사진 폴더(C:\Users\you\Pictures)를 찾을 수 없어 확인하지 못했습니다.", Verdicts.MissingSources([pictures]));
        Assert.Equal("사진, 동영상 폴더를 찾을 수 없어 확인하지 못했습니다.", Verdicts.MissingSources([pictures, videos]));
        Assert.Equal("보호하는 폴더 2개를 찾을 수 없습니다", Verdicts.MissingHeadline([pictures, videos]));
        Assert.Equal(@"새 폴더(D:\새 폴더)를 찾을 수 없어 확인하지 못했습니다.", Verdicts.MissingSources([unnamed]));
        Assert.Equal("새 폴더를 찾을 수 없습니다", Verdicts.MissingHeadline([unnamed]));
        var notice = NotificationPolicy.For(report);
        Assert.Equal("사진 폴더를 찾을 수 없습니다", notice!.Title);
        Assert.EndsWith("확인하지 못했습니다. 설정에서 폴더를 확인하세요.", notice.Text);
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

    private static RehearsalResult Rehearsed(long files, long verified) => new()
    {
        SourceName = "사진",
        SourceRoot = @"C:\Users\you\Pictures",
        DestinationId = "drive",
        DestinationName = "백업 드라이브",
        RootPath = @"E:\Backup",
        StartedUtc = Finished.AddMinutes(-47),
        FinishedUtc = Finished,
        Reachable = true,
        Files = files,
        Verified = verified,
        Sentence = string.Empty,
    };

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
