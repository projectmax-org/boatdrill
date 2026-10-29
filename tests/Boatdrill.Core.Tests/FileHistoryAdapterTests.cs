using Boatdrill.Core.Adapters;
using Boatdrill.Core.Drill;
using Boatdrill.Core.Sampling;
using Boatdrill.Core.Scoring;
using Boatdrill.Core.Verification;

namespace Boatdrill.Core.Tests;

public sealed class FileHistoryAdapterTests
{
    private static readonly SampleOptions Everything = new() { SampleSize = 1000, Seed = 1, ChurnWindow = TimeSpan.FromHours(-1), PerFolderFloor = 0 };

    /// <summary>The Data folder File History would use for a source on this machine.</summary>
    private static string DataFolderFor(string driveRoot, string sourceRoot) =>
        Path.Combine(driveRoot, "FileHistory", "max", "LAPTOP", "Data",
            MirrorFolderAdapter.VolumeFolder(sourceRoot), MirrorFolderAdapter.PathBelowVolume(sourceRoot));

    /// <summary>Copies every source file into File History as a version taken at <paramref name="takenUtc"/>.</summary>
    private static void TakeVersions(TempTree source, string driveRoot, DateTimeOffset takenUtc, IEnumerable<string>? skip = null)
    {
        var skipped = new HashSet<string>((skip ?? []).Select(p => p.Replace('\\', '/')));
        var data = DataFolderFor(driveRoot, source.Root);
        foreach (var file in Directory.EnumerateFiles(source.Root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source.Root, file);
            if (skipped.Contains(relative.Replace('\\', '/'))) continue;

            var target = Path.Combine(data, Path.GetDirectoryName(relative) ?? string.Empty, FileHistoryAdapter.VersionName(Path.GetFileName(relative), takenUtc));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static async Task<DestinationResult> Drill(TempTree source, Destination destination)
    {
        using var scratch = new TempTree();
        var report = await new DrillRunner().RunAsync(new DrillPlan
        {
            Sources = [Source.FromPath(source.Root, "Docs")],
            Destinations = [destination],
            ScratchDirectory = scratch.Root,
            Sampling = Everything,
        });
        return Assert.Single(report.Destinations);
    }

    [Theory]
    [InlineData("drive")]
    [InlineData("FileHistory")]
    [InlineData("Data")]
    public async Task NewestVersions_Verify_FromWhicheverFolderIsChosen(string chosen)
    {
        using var source = new TempTree();
        using var drive = new TempTree();
        var written = DateTimeOffset.UtcNow.AddDays(-3);
        for (var i = 0; i < 10; i++) source.Write($"docs/{i}.txt", $"draft {i}", written.AddDays(-5));
        TakeVersions(source, drive.Root, written.AddDays(-4));
        for (var i = 0; i < 10; i++) source.Write($"docs/{i}.txt", $"final {i}", written);
        TakeVersions(source, drive.Root, written.AddHours(1));

        var root = chosen switch
        {
            "FileHistory" => drive.At("FileHistory"),
            "Data" => Path.Combine(drive.Root, "FileHistory", "max", "LAPTOP", "Data"),
            _ => drive.Root,
        };
        var result = await Drill(source, Destination.FileHistory(root, "File History"));

        Assert.True(result.Reachable);
        Assert.Equal(10, result.Verified);
        Assert.Equal(Verdict.WouldRestore, result.Score.Verdict);
    }

    [Fact]
    public async Task ChangesAfterTheLastVersion_AreBehind_AndMissingFilesAreMissing()
    {
        using var source = new TempTree();
        using var drive = new TempTree();
        var old = DateTimeOffset.UtcNow.AddDays(-10);
        for (var i = 0; i < 8; i++) source.Write($"docs/{i}.txt", $"document {i}", old);
        TakeVersions(source, drive.Root, old.AddHours(1), skip: ["docs/7.txt"]);
        source.Write("docs/3.txt", "document 3, edited", DateTimeOffset.UtcNow.AddHours(-3));

        var result = await Drill(source, Destination.FileHistory(drive.Root));

        Assert.Equal(6, result.Verified);
        Assert.Equal(1, result.Outdated);
        Assert.Equal(1, result.Missing);
        var missing = Assert.Single(result.Findings, f => f.Outcome == FileOutcome.Missing);
        Assert.Contains("File History holds no version of it", missing.Detail);
    }

    [Fact]
    public async Task ADriveWithoutFileHistory_IsUnreachable()
    {
        using var source = new TempTree();
        using var drive = new TempTree();
        source.Write("a.txt", "a", DateTimeOffset.UtcNow.AddDays(-1));
        drive.Write("unrelated/b.txt", "b");

        var result = await Drill(source, Destination.FileHistory(drive.Root, "E:"));

        Assert.False(result.Reachable);
        Assert.Contains("holds no File History data", result.Sentence);
    }

    [Fact]
    public void VersionNames_RoundTrip()
    {
        var taken = new DateTimeOffset(2026, 10, 28, 14, 0, 5, TimeSpan.Zero);

        Assert.Equal("Report (2026_10_28 14_00_05 UTC).docx", FileHistoryAdapter.VersionName("Report.docx", taken));
        Assert.Equal("Makefile (2026_10_28 14_00_05 UTC)", FileHistoryAdapter.VersionName("Makefile", taken));
        Assert.Equal("archive.tar (2026_10_28 14_00_05 UTC).gz", FileHistoryAdapter.VersionName("archive.tar.gz", taken));
        Assert.Equal(taken, FileHistoryAdapter.Parse("Report (2026_10_28 14_00_05 UTC).docx", "Report", ".docx"));
        Assert.Equal(taken, FileHistoryAdapter.Parse("report (2026_10_28 14_00_05 utc).DOCX", "Report", ".docx"));
        Assert.Null(FileHistoryAdapter.Parse("Report (copy).docx", "Report", ".docx"));
        Assert.Null(FileHistoryAdapter.Parse("Report 2 (2026_10_28 14_00_05 UTC).docx", "Report", ".docx"));
    }
}
