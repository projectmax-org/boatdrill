using Boatdrill.Core.Drill;
using Boatdrill.Core.Reporting;
using Boatdrill.Core.Sampling;
using Boatdrill.Core.Verification;

namespace Boatdrill.Core.Tests;

public sealed class HtmlReportTests
{
    private static readonly SampleOptions Everything = new() { SampleSize = 1000, Seed = 1, ChurnWindow = TimeSpan.FromHours(-1), PerFolderFloor = 0 };

    /// <summary>A real drill: a mirror missing one file whose name needs escaping, and a drive that is not there.</summary>
    private static async Task<DrillReport> DrillAsync(TempTree source, TempTree mirror, string missingName)
    {
        using var scratch = new TempTree();
        return await new DrillRunner().RunAsync(new DrillPlan
        {
            Sources = [Source.FromPath(source.Root, "Docs & <Notes>")],
            Destinations = [Destination.MirrorFolder(mirror.Root, "Drive <E:>"), Destination.MirrorFolder(mirror.At("gone"), "USB stick")],
            ScratchDirectory = scratch.Root,
            Sampling = Everything,
        });
    }

    [Fact]
    public async Task TheReport_LeadsWithTheAnswer_AndEscapesEverythingItQuotes()
    {
        using var source = new TempTree();
        using var mirror = new TempTree();
        // Windows allows no < or > in a file name, so there the hostile name tries & and ' instead.
        var (hostile, escaped) = OperatingSystem.IsWindows()
            ? ("docs/Tom & Jerry's.txt", "Tom &amp; Jerry&#39;s.txt")
            : ("docs/<script>alert(1)</script>.txt", "&lt;script&gt;alert(1)&lt;/script&gt;.txt");
        for (var i = 0; i < 4; i++) source.Write($"docs/{i}.txt", $"document {i}", DateTimeOffset.UtcNow.AddDays(-5));
        source.Write(hostile, "a hostile name", DateTimeOffset.UtcNow.AddDays(-5));
        source.MirrorTo(mirror.Root, skip: [hostile]);
        var report = await DrillAsync(source, mirror, "<script>");

        var html = HtmlReport.Render(report, version: "1.2.3");

        Assert.StartsWith("<!doctype html>", html);
        Assert.EndsWith("</html>\n", html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(escaped, html);
        Assert.Contains("Docs &amp; &lt;Notes&gt;", html);
        Assert.Contains("<h1><span class=\"dot bad-fill\" aria-hidden=\"true\"></span>USB stick could not be reached</h1>", html);
        Assert.Contains("Drive &lt;E:&gt;", html);
        Assert.Contains(">Missing</span>", html);
        Assert.Contains("Made by Boatdrill 1.2.3", html);
        Assert.Contains("holds none of their contents", html);
    }

    [Fact]
    public async Task EachBackup_GetsATrend_FromTheDrillsBeforeIt()
    {
        using var source = new TempTree();
        using var mirror = new TempTree();
        for (var i = 0; i < 4; i++) source.Write($"docs/{i}.txt", $"document {i}", DateTimeOffset.UtcNow.AddDays(-5));
        source.MirrorTo(mirror.Root);
        var first = await DrillAsync(source, mirror, "none");
        var second = await DrillAsync(source, mirror, "none");

        var alone = HtmlReport.Render(second);
        var withHistory = HtmlReport.Render(second, [first, second]);

        Assert.DoesNotContain("class=\"trend\"", alone);
        Assert.Contains("aria-label=\"Score in the last 2 drills: 100, 100\"", withHistory);
        Assert.Contains("aria-label=\"Score in the last 2 drills: not reached, not reached\"", withHistory);
        Assert.Contains("class=\"miss\"", withHistory);
    }

    [Fact]
    public void Verdicts_ReadTheSameEverywhere()
    {
        Assert.Equal("would mostly restore", Verdicts.Words(Scoring.Verdict.WouldMostlyRestore));
        Assert.Equal("Changed", Verdicts.Words(FileOutcome.Rotted));
        Assert.Equal("bad", Verdicts.Tone(FileOutcome.Rotted));
        Assert.Equal("No backups were checked", Verdicts.Headline([]));
        Assert.Equal("99.6%", ScoreParts.Percent(274.0 / 275));
        Assert.Equal("100%", ScoreParts.Percent(1));
        Assert.Equal("0.4%", ScoreParts.Percent(0.004));
    }
}
