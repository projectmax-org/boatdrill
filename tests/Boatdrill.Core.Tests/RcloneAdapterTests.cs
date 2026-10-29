using Boatdrill.Core.Adapters;
using Boatdrill.Core.Drill;
using Boatdrill.Core.Sampling;
using Boatdrill.Core.Scan;
using Boatdrill.Core.Scoring;
using Boatdrill.Core.Verification;

namespace Boatdrill.Core.Tests;

/// <summary>rclone on the PATH (or named by BOATDRILL_RCLONE), with a local folder standing in for the remote.</summary>
internal static class RcloneTool
{
    public static string? Path { get; } = Find();

    static RcloneTool()
    {
        // rclone runs here never read the user's own remotes.
        Environment.SetEnvironmentVariable("RCLONE_CONFIG", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "boatdrill-tests", "rclone-none.conf"));
    }

    private static string? Find()
    {
        var named = Environment.GetEnvironmentVariable("BOATDRILL_RCLONE");
        if (!string.IsNullOrEmpty(named)) return File.Exists(named) ? named : null;

        var name = OperatingSystem.IsWindows() ? "rclone.exe" : "rclone";
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => System.IO.Path.Combine(folder, name))
            .FirstOrDefault(File.Exists);
    }
}

/// <summary>A test that needs the real rclone, skipped when it is not installed.</summary>
public sealed class RcloneFactAttribute : FactAttribute
{
    public RcloneFactAttribute()
    {
        if (RcloneTool.Path is null) Skip = "rclone is not installed: put it on the PATH or set BOATDRILL_RCLONE";
    }
}

public sealed class RcloneAdapterTests
{
    private static readonly SampleOptions Everything = new() { SampleSize = 1000, Seed = 1, ChurnWindow = TimeSpan.FromHours(-1), PerFolderFloor = 0 };

    private static readonly DateTimeOffset LastWeek = DateTimeOffset.UtcNow.AddDays(-7);

    private static Destination Remote(string location, string name = "Drive") => Destination.Rclone(location, name: name, toolPath: RcloneTool.Path);

    private static async Task<DestinationResult> DrillAsync(TempTree source, Destination destination, TempTree? scratchFolder = null)
    {
        using var own = new TempTree();
        var report = await new DrillRunner().RunAsync(new DrillPlan
        {
            Sources = [Source.FromPath(source.Root, "Docs")],
            Destinations = [destination],
            ScratchDirectory = (scratchFolder ?? own).Root,
            Sampling = Everything,
        });
        return Assert.Single(report.Destinations);
    }

    private static void WriteDocuments(TempTree tree, int count)
    {
        for (var i = 0; i < count; i++) tree.Write($"docs/{i}.txt", $"document {i}", LastWeek);
    }

    [RcloneFact]
    public async Task ARemoteHoldingTheFoldersContents_Verifies()
    {
        using var source = new TempTree();
        using var remote = new TempTree();
        WriteDocuments(source, 10);
        source.Write("notes/#ideas.txt", "a name rclone could read as a comment", LastWeek);
        source.Write("notes/plan [draft].txt", "a name that looks like a pattern", LastWeek);
        source.MirrorTo(remote.Root);

        var result = await DrillAsync(source, Remote(":local:" + remote.Root));

        Assert.True(result.Reachable, result.ProbeDetail);
        Assert.Equal(DestinationKind.Rclone, result.Kind);
        Assert.Equal(12, result.Verified);
        Assert.Equal(Verdict.WouldRestore, result.Score.Verdict);
        Assert.Equal(MirrorLayout.Contents, Assert.Single(result.Bindings).Layout);
        Assert.True(result.BytesRead > 0);
    }

    [RcloneFact]
    public async Task AFolderNamedAfterTheSource_IsFound()
    {
        using var source = new TempTree();
        using var remote = new TempTree();
        WriteDocuments(source, 6);
        source.MirrorTo(Path.Combine(remote.Root, Source.DisplayName(source.Root)));

        var result = await DrillAsync(source, Remote(remote.Root));

        Assert.Equal(6, result.Verified);
        Assert.Equal(MirrorLayout.RootFolder, Assert.Single(result.Bindings).Layout);
    }

    [RcloneFact]
    public async Task ChangesAfterTheCopy_AreBehind_AndFilesNeverCopiedAreMissing()
    {
        using var source = new TempTree();
        using var remote = new TempTree();
        WriteDocuments(source, 8);
        source.MirrorTo(remote.Root, skip: ["docs/7.txt"]);
        source.Write("docs/3.txt", "document 3, edited", DateTimeOffset.UtcNow.AddHours(-2));

        var result = await DrillAsync(source, Remote(remote.Root));

        Assert.Equal((6, 1, 1), (result.Verified, result.Outdated, result.Missing));
        var missing = Assert.Single(result.Findings, f => f.Outcome == FileOutcome.Missing);
        Assert.Equal("not on " + remote.Root, missing.Detail);
    }

    [RcloneFact]
    public async Task ADrill_LeavesTheRemoteAsItWas_AndEmptiesTheScratchFolder()
    {
        using var source = new TempTree();
        using var remote = new TempTree();
        using var scratch = new TempTree();
        WriteDocuments(source, 6);
        source.MirrorTo(remote.Root);
        string Fingerprint() => string.Join("\n", Directory.EnumerateFileSystemEntries(remote.Root, "*", SearchOption.AllDirectories).Order()
            .Select(p => $"{p} {File.GetLastWriteTimeUtc(p).Ticks} {(File.Exists(p) ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p))) : "dir")}"));
        var before = Fingerprint();

        var result = await DrillAsync(source, Remote(remote.Root), scratch);

        Assert.Equal(6, result.Verified);
        Assert.Equal(before, Fingerprint());
        Assert.Empty(Directory.EnumerateFileSystemEntries(scratch.Root));
    }

    [RcloneFact]
    public async Task AMissingPath_OrAnUnknownRemote_IsUnreachable_AndSaysWhy()
    {
        using var source = new TempTree();
        using var remote = new TempTree();
        WriteDocuments(source, 2);

        var gone = await DrillAsync(source, Remote(remote.At("not there")));
        Assert.False(gone.Reachable);
        Assert.Equal(remote.At("not there") + " was not found", gone.ProbeDetail);
        Assert.EndsWith("Check the remote in Settings, then run a drill.", gone.Sentence);

        var unknown = await DrillAsync(source, Remote("nosuchremote:Backups"));
        Assert.False(unknown.Reachable);
        Assert.Equal("rclone has no remote named \"nosuchremote\"; set it up with rclone config first", unknown.ProbeDetail);
    }

    [Fact]
    public async Task WithoutRclone_TheRemoteIsUnreachable_AndSaysWhy()
    {
        using var source = new TempTree();
        using var folder = new TempTree();
        WriteDocuments(source, 2);

        var result = await DrillAsync(source, Destination.Rclone("gdrive:Backups", toolPath: folder.At("no-such-rclone")));

        Assert.False(result.Reachable);
        Assert.Equal("rclone could not be started; install it, or say where it is in Settings", result.ProbeDetail);
    }

    [RcloneFact]
    public async Task AScan_ListsTheRemoteOnce_AndARehearsal_DownloadsItInBatches()
    {
        using var source = new TempTree();
        using var remote = new TempTree();
        using var scratch = new TempTree();
        for (var i = 0; i < 10; i++) source.Write($"docs/{i:00}.txt", new string('c', 50), LastWeek);
        source.MirrorTo(remote.Root, skip: ["docs/09.txt"]);

        var scan = await new OnlyHereScanner().ScanAsync([Source.FromPath(source.Root, "Docs")], [Remote(remote.Root)], scratch.Root);
        Assert.Equal(1, Assert.Single(scan.Sources).NoCopyFiles);

        var rehearsal = await new RehearsalRunner().RunAsync(new RehearsalPlan(Source.FromPath(source.Root, "Docs"), Remote(remote.Root), scratch.Root)
        {
            BatchBytes = 120,
        });
        Assert.Equal((10L, 9L, 1L), (rehearsal.Files, rehearsal.Verified, rehearsal.Missing));
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(scratch.Root, "*", SearchOption.AllDirectories), File.Exists);
    }

    [Fact]
    public void Locations_JoinAndName_AsRcloneWritesThem()
    {
        Assert.Equal("gdrive:Docs", RcloneAdapter.Join("gdrive:", "Docs"));
        Assert.Equal("gdrive:Backup/Docs", RcloneAdapter.Join("gdrive:Backup/", "Docs"));

        var destination = Destination.Rclone("  gdrive:Backups/Laptop ");
        Assert.Equal("gdrive:Backups/Laptop", destination.RootPath);
        Assert.Equal("Laptop", destination.Name);
        Assert.Equal(destination.Id, Destination.Rclone("gdrive:Backups/Laptop").Id);
        Assert.NotEqual(destination.Id, Destination.Rclone("gdrive:Backups/Desktop").Id);

        var entries = RcloneAdapter.ParseListing("""
            [{"Path":"a.txt","Name":"a.txt","Size":4,"ModTime":"2026-10-01T12:00:00.123456789Z","IsDir":false},
             {"Path":"sub","Name":"sub","Size":-1,"ModTime":"2026-10-29T07:34:45Z","IsDir":true}]
            """);
        var entry = Assert.Single(entries);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero).AddTicks(1_234_567), entry.ModifiedUtc);
    }
}
