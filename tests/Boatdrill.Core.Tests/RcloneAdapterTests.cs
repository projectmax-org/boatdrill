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
        TempTree.AssertEmpty(scratch.Root);
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

    [RcloneFact]
    public async Task ARemoteWithoutTheFolder_HoldsNoCopyOfIt_WhichIsNotAListThatFailed()
    {
        using var source = new TempTree();
        using var remote = new TempTree();
        using var scratch = new TempTree();
        WriteDocuments(source, 6);
        remote.Write("elsewhere.txt", "another folder's backup", LastWeek);
        var drive = Remote(remote.Root);

        // The remote holds none of the files at its top, and has neither a folder named after the source nor one for
        // its full path. Of those rclone says "directory not found", which is an answer, not a list it could not make.
        var result = await DrillAsync(source, drive, scratch);

        Assert.True(result.Reachable, result.ProbeDetail);
        Assert.Equal((0, 6, 0), (result.Verified, result.Missing, result.Errors));
        Assert.Null(Assert.Single(result.Bindings).Problem);
        Assert.All(result.Findings, f => Assert.Equal("not on " + remote.Root, f.Detail));
        Assert.Equal(
            $"Drive would not restore. 0 of 6 sampled files read back correctly and 6 have no copy. No copy of Docs was found under {remote.Root}.",
            result.Sentence);

        // A scan has looked in it, and found nothing there.
        var scan = await new OnlyHereScanner().ScanAsync([Source.FromPath(source.Root, "Docs")], [drive], scratch.Root);
        Assert.Equal(["Drive"], scan.Checked);
        Assert.Empty(scan.Unreachable);
        Assert.Equal(6, Assert.Single(scan.Sources).NoCopyFiles);
    }

    [Fact]
    public async Task AListThatFails_IsNotARemoteThatHoldsNothing()
    {
        using var source = new TempTree();
        using var tool = new TempTree();
        using var scratch = new TempTree();
        WriteDocuments(source, 3);
        var docs = Source.FromPath(source.Root, "Docs");

        // rclone's listings are told apart by the word after "-q --ask-password=false lsjson": --max-depth for the first
        // look at the remote, -R for the files in it. A .cmd counts --ask-password=false as two words.
        var word = OperatingSystem.IsWindows() ? 5 : 4;

        // rclone answers the first look and then cannot list the files, as when a sign-in has expired. A stand-in says
        // so: a folder on this PC, which is what the other tests give the real rclone for a remote, does not fail that way.
        const string said = "its files could not be listed: rclone said: Backups: error listing: couldn't list directory: oauth2: token expired and refresh token is not set";
        var failing = Destination.Rclone("remote:Backups", name: "Drive", toolPath: StandIn.Write(tool, "rclone", word,
        [
            new("--max-depth", Output: "[]\n"),
            new("-R", ExitCode: 1, Error: "2026/11/21 09:30:00 ERROR : Backups: error listing: couldn't list directory: oauth2: token expired and refresh token is not set\n"),
        ]));

        var result = await DrillAsync(source, failing, scratch);
        Assert.True(result.Reachable, result.ProbeDetail);
        Assert.Equal((0, 0, 3), (result.Verified, result.Missing, result.Errors));
        Assert.All(result.Findings, f => Assert.Equal((FileOutcome.Error, said), (f.Outcome, f.Detail)));
        Assert.Equal($"Drive would not restore. 0 of 3 sampled files read back correctly and 3 could not be read. Docs: {said}.", result.Sentence);

        var scan = await new OnlyHereScanner().ScanAsync([docs], [failing], scratch.Root);
        Assert.Empty(scan.Checked);
        Assert.Equal($"Drive: {said}", Assert.Single(scan.Unreachable));
        Assert.Equal(0, scan.OnlyHereFiles);

        // Told "directory not found" for every place the copies could be, it has its answer: there is no copy.
        var empty = Destination.Rclone("remote:Backups", name: "Drive", toolPath: StandIn.Write(tool, "rclone-empty", word,
        [
            new("--max-depth", Output: "[]\n"),
            new("-R", ExitCode: 3, Error: "2026/11/21 09:30:00 ERROR : : error listing: directory not found\n"),
        ]));

        var none = await DrillAsync(source, empty, scratch);
        Assert.Equal((0, 3, 0), (none.Verified, none.Missing, none.Errors));
        Assert.Null(Assert.Single(none.Bindings).Problem);

        var looked = await new OnlyHereScanner().ScanAsync([docs], [empty], scratch.Root);
        Assert.Equal(["Drive"], looked.Checked);
        Assert.Empty(looked.Unreachable);
        Assert.Equal(3, Assert.Single(looked.Sources).NoCopyFiles);
    }

    [Fact]
    public async Task ALocationThatIsOrHoldsTheProtectedFolder_IsNotLookedInForItsCopies()
    {
        // rclone takes a folder on this PC for a location as readily as a remote. With the protected folder in it, the
        // folder "named after the source" is the source: rclone would download the originals, and each would verify.
        using var tree = new TempTree();
        using var scratch = new TempTree();
        for (var i = 0; i < 4; i++) tree.Write($"OneDrive/Documents/{i}.txt", $"document {i}", LastWeek);
        var source = Source.FromPath(tree.At("OneDrive/Documents"), "Documents");
        static MirrorLayout[] Layouts(string location, Source source) =>
            RcloneAdapter.Usable(Destination.Rclone(location), source).Select(c => c.Layout).ToArray();

        Assert.Equal([MirrorLayout.Contents, MirrorLayout.FullPath], Layouts(tree.At("OneDrive"), source));
        Assert.Equal([MirrorLayout.Contents, MirrorLayout.FullPath], Layouts(tree.At("OneDrive") + "/", source));
        Assert.Equal([MirrorLayout.Contents, MirrorLayout.FullPath], Layouts(":local:" + tree.At("OneDrive"), source));
        Assert.Equal([MirrorLayout.RootFolder, MirrorLayout.FullPath], Layouts(tree.At("OneDrive/Documents"), source));

        // Through a junction or a symbolic link, the folder in it is the protected folder all the same.
        Assert.True(ScratchTests.TryLink(tree.At("Dropbox"), tree.At("OneDrive")), "No link could be made to test with.");
        Assert.Equal([MirrorLayout.Contents, MirrorLayout.FullPath], Layouts(tree.At("Dropbox"), source));

        // A remote is somewhere else, whatever its folders are called, and so is another folder on this PC.
        Assert.Equal(3, Layouts("gdrive:OneDrive", source).Length);
        Assert.Equal(3, Layouts("gdrive:", source).Length);
        Assert.Equal(3, Layouts(scratch.Root, source).Length);

        // Told to use that layout, it finds nothing and says why, and rclone is not run to list or download anything.
        var told = Destination.Rclone(tree.At("OneDrive"), name: "Drive", toolPath: tree.At("no-such-rclone")) with { Layout = MirrorLayout.RootFolder };
        var adapter = new RcloneAdapter();
        var binding = await adapter.BindAsync(told, new Sampler(Everything).Take(source), scratch.Root, BindPurpose.Read, [], CancellationToken.None);

        Assert.True(binding.NothingThere);
        Assert.Equal($"the copies would be looked for in {source.RootPath}, the protected folder itself", binding.Problem);
        var copy = await adapter.LocateAsync(binding, "0.txt", CancellationToken.None);
        Assert.Equal((CopyState.Missing, binding.Problem), (copy.State, copy.Detail));
        await adapter.PrefetchAsync(binding, ["0.txt"], CancellationToken.None);
    }

    [Fact]
    public void ALocation_IsAFolderOnThisPc_WhenRcloneWouldReadItAsOne()
    {
        using var tree = new TempTree();

        Assert.Null(RcloneAdapter.LocalFolder("gdrive:Backups/Laptop"));
        Assert.Null(RcloneAdapter.LocalFolder("gdrive:"));
        Assert.Null(RcloneAdapter.LocalFolder(":s3:bucket/Laptop"));

        Assert.Equal(tree.Root, RcloneAdapter.LocalFolder(tree.Root));
        Assert.Equal(tree.Root, RcloneAdapter.LocalFolder(":local:" + tree.Root));
        Assert.Equal(tree.At("Docs"), RcloneAdapter.LocalFolder(RcloneAdapter.Join(tree.Root, "Docs")));

        // Without a colon it can only be a path, from the folder rclone is started in, which is Boatdrill's own.
        Assert.Equal(Path.GetFullPath("Backups"), RcloneAdapter.LocalFolder("Backups"));

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(@"C:\Backups\Laptop", RcloneAdapter.LocalFolder("C:/Backups/Laptop"));
            Assert.Equal(@"\\nas\backup\Laptop", RcloneAdapter.LocalFolder(@"\\nas\backup\Laptop"));
        }
        else
        {
            // Only Windows has drive letters: elsewhere "C:" is a remote named C, and a colon after a slash is part of a name.
            Assert.Null(RcloneAdapter.LocalFolder(@"C:\Backups\Laptop"));
            Assert.Equal("/mnt/backup/a:b", RcloneAdapter.LocalFolder("/mnt/backup/a:b"));
        }
    }

    [RcloneFact]
    public async Task ALocationThatHoldsTheProtectedFolder_IsNotReadAsItsBackup()
    {
        using var tree = new TempTree();
        using var scratch = new TempTree();
        for (var i = 0; i < 8; i++) tree.Write($"OneDrive/Documents/{i}.txt", $"document {i}", LastWeek);
        var plan = new DrillPlan
        {
            Sources = [Source.FromPath(tree.At("OneDrive/Documents"), "Documents")],
            Destinations = [Remote(tree.At("OneDrive"))],
            ScratchDirectory = scratch.Root,
            Sampling = Everything,
        };

        // Not one original is downloaded and compared with itself: whether the files then count as having no copy or
        // as not listed is rclone's to say of the layouts that are left.
        var result = Assert.Single((await new DrillRunner().RunAsync(plan)).Destinations);
        Assert.True(result.Reachable, result.ProbeDetail);
        Assert.Equal((0, 8), (result.Verified, result.Missing + result.Errors));
        Assert.Equal(Verdict.WouldNotRestore, result.Score.Verdict);
        Assert.NotEqual(MirrorLayout.RootFolder, Assert.Single(result.Bindings).Layout);

        var told = await new DrillRunner().RunAsync(plan with { Destinations = [Remote(tree.At("OneDrive")) with { Layout = MirrorLayout.RootFolder }] });
        var refused = Assert.Single(told.Destinations);
        Assert.Equal((0, 8), (refused.Verified, refused.Missing));
        Assert.Contains("the protected folder itself", refused.Sentence);
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
