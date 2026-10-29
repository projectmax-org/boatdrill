using Boatdrill.Core.Drill;
using Boatdrill.Core.Scan;
using Boatdrill.Core.Text;

namespace Boatdrill.Core.Tests;

public sealed class OnlyHereScannerTests
{
    private static readonly DateTimeOffset Old = DateTimeOffset.UtcNow.AddDays(-30);

    private static async Task<OnlyHereReport> ScanAsync(TempTree source, params Destination[] destinations)
    {
        using var scratch = new TempTree();
        return await new OnlyHereScanner().ScanAsync([Source.FromPath(source.Root, "Docs")], destinations, scratch.Root);
    }

    [Fact]
    public async Task FilesWithNoCurrentCopy_AreListed_LargestFirst()
    {
        using var source = new TempTree();
        using var mirror = new TempTree();
        for (var i = 0; i < 10; i++) source.Write($"docs/{i}.txt", new string('d', 100 + i), Old);
        source.Write("photos/2026/a.jpg", new string('p', 5000), Old);
        source.Write("photos/2026/b.jpg", new string('p', 3000), Old);
        source.MirrorTo(mirror.Root, skip: ["photos/2026/a.jpg", "photos/2026/b.jpg", "docs/9.txt"]);
        source.Write("docs/0.txt", "edited after the backup", DateTimeOffset.UtcNow.AddHours(-1));

        var report = await ScanAsync(source, Destination.MirrorFolder(mirror.Root, "Drive"));

        var summary = Assert.Single(report.Sources);
        Assert.Equal(12, summary.Files);
        Assert.Equal(3, summary.NoCopyFiles);
        Assert.Equal(1, summary.OlderFiles);
        Assert.Equal(["a.jpg", "b.jpg", "9.txt", "0.txt"], report.Largest.Select(f => Path.GetFileName(f.RelativePath)));
        Assert.True(report.Largest[^1].HasOlderCopy);
        Assert.False(report.Largest[0].HasOlderCopy);

        var photos = report.Folders[0];
        Assert.Equal(Path.Combine("photos", "2026"), photos.RelativePath);
        Assert.Equal((2, 8000L), (photos.Files, photos.Bytes));
        Assert.Equal(["Drive"], report.Checked);
        Assert.Equal(
            $"4 of 12 files ({Humanize.Bytes(8132)}) exist only on this PC: 3 have no copy in any backup and 1 changed after its last copy.",
            report.Sentence);
    }

    [Fact]
    public async Task AFileInAnyBackup_IsNotOnlyHere()
    {
        using var source = new TempTree();
        using var first = new TempTree();
        using var second = new TempTree();
        source.Write("x.txt", "x", Old);
        source.Write("y.txt", "y", Old);
        source.MirrorTo(first.Root, skip: ["x.txt"]);
        source.MirrorTo(second.Root, skip: ["y.txt"]);

        var report = await ScanAsync(source, Destination.MirrorFolder(first.Root, "First"), Destination.MirrorFolder(second.Root, "Second"));

        Assert.Equal(0, report.OnlyHereFiles);
        Assert.Empty(report.Largest);
        Assert.Equal("All 2 files have a current copy in at least one backup.", report.Sentence);
    }

    [Fact]
    public async Task UnreachableBackups_AreNamed_AndWithoutAnyNothingIsCompared()
    {
        using var source = new TempTree();
        using var mirror = new TempTree();
        source.Write("x.txt", "x", Old);
        var gone = Destination.MirrorFolder(mirror.At("unplugged"), "USB drive");

        var none = await ScanAsync(source, gone);
        Assert.Empty(none.Checked);
        Assert.Empty(none.Sources);
        Assert.Equal($"USB drive: {mirror.At("unplugged")} was not found", Assert.Single(none.Unreachable));
        Assert.Equal("None of the backups could be reached, so nothing was compared.", none.Sentence);

        var some = await ScanAsync(source, gone, Destination.MirrorFolder(mirror.Root, "Drive"));
        Assert.Equal(1, some.OnlyHereFiles);
        Assert.EndsWith("1 backup could not be reached and was not looked in.", some.Sentence);
    }

    [Fact]
    public async Task TheCanaryAndExcludedFiles_AreNotCounted()
    {
        using var source = new TempTree();
        using var mirror = new TempTree();
        source.Write("kept.txt", "kept", Old);
        source.Write("cache/big.tmp", "excluded", Old);
        Canary.Stamp(source.Root, DateTimeOffset.UtcNow);
        using var scratch = new TempTree();

        var report = await new OnlyHereScanner().ScanAsync(
            [Source.FromPath(source.Root, "Docs") with { Excludes = ["cache"] }], [Destination.MirrorFolder(mirror.Root, "Drive")], scratch.Root);

        var summary = Assert.Single(report.Sources);
        Assert.Equal(1, summary.Files);
        Assert.Equal("kept.txt", Assert.Single(report.Largest).RelativePath);
    }
}
