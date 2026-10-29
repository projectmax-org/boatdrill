using System.Globalization;
using Boatdrill.Core.Adapters;
using Boatdrill.Core.Drill;
using Boatdrill.Core.History;
using Boatdrill.Core.Scan;
using Boatdrill.Core.Scoring;
using Boatdrill.Core.Tools;
using Boatdrill.Core.Verification;
using static Boatdrill.Core.Tests.ResticAdapterTests;

namespace Boatdrill.Core.Tests;

public sealed class KopiaAdapterTests
{
    [KopiaFact]
    public async Task ASnapshot_Verifies_ByReadingTheSampleBack()
    {
        using var source = new TempTree();
        using var repository = new KopiaRepository();
        WriteDocuments(source, 12, LastWeek);
        source.Write("photos/2026/beach.raw", RandomBytes(300_000), LastWeek);
        repository.Backup(source.Root);

        var result = await DrillOnce(source.Root, repository.Destination());

        Assert.True(result.Reachable, result.ProbeDetail);
        Assert.Equal(DestinationKind.Kopia, result.Kind);
        Assert.Equal("Repository holds 1 snapshot", result.ProbeDetail?.Replace(repository.Location, "Repository"));
        Assert.Equal(13, result.Verified);
        Assert.Empty(result.Findings);
        Assert.Equal(Verdict.WouldRestore, result.Score.Verdict);
        var binding = Assert.Single(result.Bindings);
        Assert.Equal(13, binding.ProbeHits);
        Assert.Contains(", snapshot ", binding.BasePath);
        Assert.True(result.BytesRead >= 300_000);
    }

    [KopiaFact]
    public async Task ChangesAfterTheSnapshot_AreBehind_AndNewFilesAreMissing()
    {
        using var source = new TempTree();
        using var repository = new KopiaRepository();
        WriteDocuments(source, 7, LastWeek);
        repository.Backup(source.Root);
        source.Write("docs/7.txt", "made after the snapshot", LastWeek.AddDays(1));
        source.Write("docs/3.txt", "document 3, edited", DateTimeOffset.UtcNow.AddHours(-3));

        var result = await DrillOnce(source.Root, repository.Destination());

        Assert.Equal(6, result.Verified);
        Assert.Equal(1, result.Outdated);
        Assert.Equal(1, result.Missing);
        var missing = Assert.Single(result.Findings, f => f.Outcome == FileOutcome.Missing);
        Assert.Equal("docs/7.txt", missing.RelativePath.Replace('\\', '/'));
        Assert.StartsWith("not in snapshot ", missing.Detail);
        var behind = Assert.Single(result.Findings, f => f.Outcome == FileOutcome.Outdated);
        Assert.Equal(LastWeek.UtcDateTime, behind.CopyWriteUtc!.Value.UtcDateTime, TimeSpan.FromSeconds(1));
        Assert.NotNull(result.BehindSince);
    }

    [KopiaFact]
    public async Task TheNewestSnapshotThatHoldsTheSource_IsDrilled()
    {
        using var source = new TempTree();
        using var other = new TempTree();
        using var repository = new KopiaRepository();
        WriteDocuments(source, 6, LastWeek.AddDays(-7));
        repository.Backup(source.Root);
        WriteDocuments(source, 6, LastWeek);
        repository.Backup(source.Root);
        other.Write("unrelated.txt", "made last, holds none of the source");
        repository.Backup(other.Root);

        var result = await DrillOnce(source.Root, repository.Destination());

        Assert.Equal("Repository holds 3 snapshots", result.ProbeDetail?.Replace(repository.Location, "Repository"));
        Assert.Equal(6, result.Verified);
    }

    [KopiaFact]
    public async Task ASnapshotOfAParentFolder_HoldsTheSource()
    {
        using var tree = new TempTree();
        using var repository = new KopiaRepository();
        WriteDocuments(tree, 5, LastWeek);
        tree.Write("music/song.txt", "not part of the source", LastWeek);
        repository.Backup(tree.Root);

        var result = await DrillOnce(tree.At("docs"), repository.Destination());

        Assert.Equal(5, result.Sampled);
        Assert.Equal(5, result.Verified);
    }

    [KopiaFact]
    public async Task SnapshotsOfFoldersInsideTheSource_EachHoldTheirPart()
    {
        using var tree = new TempTree();
        using var repository = new KopiaRepository();
        WriteDocuments(tree, 4, LastWeek);
        tree.Write("photos/beach.raw", RandomBytes(50_000), LastWeek);
        tree.Write("loose.txt", "in no snapshot", LastWeek);
        repository.Backup(tree.At("docs"));
        repository.Backup(tree.At("photos"));

        var result = await DrillOnce(tree.Root, repository.Destination());

        Assert.Equal(5, result.Verified);
        Assert.Equal(1, result.Missing);
        Assert.Contains("snapshots of 2 folders inside it", Assert.Single(result.Bindings).BasePath);
        Assert.Equal("no snapshot in the repository holds its folder", Assert.Single(result.Findings).Detail);
    }

    [KopiaFact]
    public async Task NoSnapshotHoldsTheSource_SaysSo()
    {
        using var source = new TempTree();
        using var other = new TempTree();
        using var repository = new KopiaRepository();
        WriteDocuments(source, 4, LastWeek);
        other.Write("elsewhere.txt", "x", LastWeek);
        repository.Backup(other.Root);

        var result = await DrillOnce(source.Root, repository.Destination());

        Assert.True(result.Reachable);
        Assert.Equal(4, result.Missing);
        Assert.Equal(Verdict.WouldNotRestore, result.Score.Verdict);
        Assert.Contains($"Docs: no snapshot in the repository holds {source.Root}.", result.Sentence);
    }

    [KopiaFact]
    public async Task CopiesVerifiedBefore_AreReadAgain_AndANewVersionIsNotRot()
    {
        using var source = new TempTree();
        using var repository = new KopiaRepository();
        WriteDocuments(source, 6, LastWeek);
        repository.Backup(source.Root);
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var ledger = new HashLedger();
        Assert.Equal(6, (await DrillOnce(source.Root, repository.Destination(), ledger, clock)).Verified);

        // The same size and date, other bytes, backed up again with every file read: a new version, not decay.
        source.Write("docs/2.txt", "DOCUMENT 2", LastWeek);
        repository.Backup(source.Root, "--force-hash", "100");
        clock.Now = clock.Now.AddDays(2);
        var second = await DrillOnce(source.Root, repository.Destination(), ledger, clock, excludes: ["1.txt", "2.txt"]);

        Assert.Equal(4, second.Verified);
        Assert.Equal(1, second.Rechecked);
        Assert.Equal(0, second.Rotted);
        Assert.Empty(second.Findings);
        Assert.DoesNotContain(ledger.Entries, e => e.RelativePath.EndsWith("2.txt", StringComparison.Ordinal));
        Assert.Contains(ledger.Entries, e => e.RelativePath.EndsWith("1.txt", StringComparison.Ordinal) && e.VerifiedUtc == clock.Now);
    }

    [KopiaFact]
    public async Task TheCanary_IsReadWithTheSample_AndShowsTheMissedStamp()
    {
        using var source = new TempTree();
        using var repository = new KopiaRepository();
        WriteDocuments(source, 3, LastWeek);
        var clock = new TestClock(DateTimeOffset.UtcNow.AddDays(-3));
        Canary.Stamp(source.Root, clock.Now);
        repository.Backup(source.Root);
        Canary.Stamp(source.Root, clock.Now.AddDays(1));
        var stamps = Canary.Read(source.Root);
        clock.Now = clock.Now.AddDays(3);

        var result = await DrillOnce(source.Root, repository.Destination(), clock: clock, canaries: true);

        Assert.Equal(3, result.Verified);
        Assert.Equal(stamps[1], result.CanaryCopiedUtc);
        Assert.Equal(stamps[0], result.CanaryBehindSince);
    }

    [KopiaFact]
    public async Task AScan_LooksFilesUpInTheSnapshotsListing_AndLeavesNothingBehind()
    {
        using var source = new TempTree();
        using var repository = new KopiaRepository();
        using var scratch = new TempTree();
        WriteDocuments(source, 5, LastWeek);
        repository.Backup(source.Root);
        source.Write("docs/new.txt", "made after the snapshot", LastWeek.AddDays(1));
        source.Write("docs/0.txt", "document 0, edited", LastWeek.AddDays(1));

        var report = await new OnlyHereScanner().ScanAsync([Source.FromPath(source.Root, "Docs")], [repository.Destination()], scratch.Root);

        var summary = Assert.Single(report.Sources);
        Assert.Equal(6, summary.Files);
        Assert.Equal(1, summary.NoCopyFiles);
        Assert.Equal(1, summary.OlderFiles);
        Assert.Empty(Directory.EnumerateFileSystemEntries(scratch.Root));
    }

    [KopiaFact]
    public async Task ARehearsal_ReadsTheFolderBack_AndLeavesNothingBehind()
    {
        using var source = new TempTree();
        using var repository = new KopiaRepository();
        using var scratch = new TempTree();
        WriteDocuments(source, 6, LastWeek);
        repository.Backup(source.Root);

        var result = await new RehearsalRunner().RunAsync(
            new RehearsalPlan(Source.FromPath(source.Root, "Docs"), repository.Destination(), scratch.Root) { Folder = "docs" });

        Assert.Equal(6, result.Verified);
        Assert.Empty(Directory.EnumerateFileSystemEntries(scratch.Root));
    }

    [KopiaFact]
    public async Task NamesWithSpacesAndBrackets_AreListedAndRead()
    {
        using var source = new TempTree();
        using var repository = new KopiaRepository();
        source.Write("docs/report [final].txt", "brackets", LastWeek);
        source.Write("docs/two  spaces.txt", "spaces", LastWeek);
        source.Write("docs/회의록 10월.txt", "Korean", LastWeek);
        if (!OperatingSystem.IsWindows()) source.Write("docs/ leading space.txt", "a name kopia pads around", LastWeek);
        repository.Backup(source.Root);

        var result = await DrillOnce(source.Root, repository.Destination());

        Assert.Equal(OperatingSystem.IsWindows() ? 3 : 4, result.Verified);
        Assert.Empty(result.Findings);
    }

    [KopiaFact]
    public async Task AnExistingConnectionsFile_LeadsToTheSameRepository()
    {
        using var source = new TempTree();
        using var repository = new KopiaRepository();
        WriteDocuments(source, 4, LastWeek);
        repository.Backup(source.Root);
        var destination = Destination.Kopia(repository.ConfigFile, KopiaRepository.Password, toolPath: KopiaRepository.Tool);

        var result = await DrillOnce(source.Root, destination);

        Assert.Equal("Kopia", destination.Name);
        Assert.True(result.Reachable, result.ProbeDetail);
        Assert.Equal(4, result.Verified);
    }

    // ---------------- what kopia prints, as its source writes it ----------------

    [Fact]
    public void SnapshotLists_Parse_KeepingFinishedSnapshotsOfFolders()
    {
        var snapshots = KopiaAdapter.ParseSnapshots("""
            [{"id":"0f1e2d3c4b5a69788796a5b4c3d2e1f0","source":{"host":"laptop","userName":"max","path":"C:\\Users\\max\\Documents"},"description":"",
              "startTime":"2026-10-19T12:00:00.123456789Z","endTime":"2026-10-19T12:01:00Z",
              "stats":{"totalSize":12,"excludedTotalSize":0,"fileCount":1,"cachedFiles":0,"nonCachedFiles":1,"dirCount":1,"excludedFileCount":0,"excludedDirCount":0,"ignoredErrorCount":0,"errorCount":0},
              "rootEntry":{"name":"Documents","type":"d","mode":"0777","mtime":"2026-10-19T11:00:00Z","obj":"k5d1e0b4c2a","summ":{"size":12,"files":1,"symlinks":0,"dirs":1,"maxTime":"2026-10-19T11:00:00Z","numFailed":0}},
              "retentionReason":["latest-1","hourly-1"]},
             {"id":"aa","source":{"host":"laptop","userName":"max","path":"C:\\Users\\max\\Documents"},"startTime":"2026-10-19T13:00:00Z",
              "incomplete":"checkpoint","rootEntry":{"name":"Documents","type":"d","obj":"k77"}},
             {"id":"bb","source":{"host":"laptop","userName":"max","path":"C:\\notes.txt"},"startTime":"2026-10-19T13:00:00Z",
              "rootEntry":{"name":"notes.txt","type":"f","obj":"0123"}},
             {"id":"cc","source":{"host":"laptop","userName":"max","path":"C:\\x"},"startTime":"2026-10-19T13:00:00Z"}]
            """);

        var snapshot = Assert.Single(snapshots);
        Assert.Equal(new KopiaAdapter.Snapshot("0f1e2d3c4b5a69788796a5b4c3d2e1f0",
            new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero).AddTicks(1_234_567), "laptop", @"C:\Users\max\Documents", "k5d1e0b4c2a"), snapshot);
        Assert.Empty(KopiaAdapter.ParseSnapshots("[\n]"));
        Assert.Empty(KopiaAdapter.ParseSnapshots("not json"));
    }

    [Fact]
    public void FolderObjects_Parse_AsKopiaStoresThem()
    {
        var entries = KopiaAdapter.ParseDirectory("""
            {"stream":"kopia:directory","entries":[
              {"name":"Documents","type":"d","mode":"0755","mtime":"2026-10-19T12:00:00.123456789Z","obj":"k0a1b2c",
               "summ":{"size":10,"files":1,"symlinks":0,"dirs":1,"maxTime":"2026-10-19T12:00:00Z","numFailed":0}},
              {"name":"a.txt","type":"f","mode":"0644","mtime":"2026-10-19T12:00:00Z","obj":"0a1b2c3d","size":5},
              {"name":"link","type":"s","obj":"0f"}],
             "summary":{"size":10,"files":1,"symlinks":1,"dirs":1,"maxTime":"2026-10-19T12:00:00Z","numFailed":0}}
            """);

        Assert.Equal([new KopiaAdapter.Entry("Documents", "d", "k0a1b2c"), new KopiaAdapter.Entry("a.txt", "f", "0a1b2c3d"), new KopiaAdapter.Entry("link", "s", "0f")], entries);
        Assert.True(entries[0].IsFolder);
        Assert.False(entries[1].IsFolder);
        Assert.Empty(KopiaAdapter.ParseDirectory("""{"stream":"kopia:directory"}"""));
        Assert.Empty(KopiaAdapter.ParseDirectory("garbage"));
    }

    /// <summary>A line as `kopia ls -l` prints it, with Go's "%v %12s %v %-34v %v": C#'s alignment pads the same way.</summary>
    private static string Line(string mode, long size, DateTime local, string objectId, string name) =>
        string.Format(CultureInfo.InvariantCulture, "{0} {1,12} {2} {3,-34} {4}", mode, size, local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " KST", objectId, name);

    [Fact]
    public void ListingLines_Parse_WhateverTheNameHolds()
    {
        var local = new DateTime(2026, 10, 19, 21, 0, 5, DateTimeKind.Local);
        var utc = new DateTimeOffset(local).ToUniversalTime();
        const string Short = "0123456789abcdef0123456789abcdef";
        var longId = "I" + new string('a', 64);

        Assert.Equal(new KopiaAdapter.Node("docs/a.txt", Short, 11, utc), KopiaAdapter.ParseListLine(Line("-rw-r--r--", 11, local, Short, "docs/a.txt")));
        Assert.Equal(new KopiaAdapter.Node("docs/  two  spaces .txt", Short, 0, utc), KopiaAdapter.ParseListLine(Line("-rw-rw-rw-", 0, local, Short, "docs/  two  spaces .txt")));
        Assert.Equal(new KopiaAdapter.Node(" leading.txt", longId, 1_234_567_890_123, utc), KopiaAdapter.ParseListLine(Line("-rw-------", 1_234_567_890_123, local, longId, " leading.txt")));
        Assert.Equal("회의록 10월.txt", KopiaAdapter.ParseListLine(Line("urwxr-xr-x", 3, local, Short, "회의록 10월.txt") + "\r")?.Path);

        Assert.Null(KopiaAdapter.ParseListLine(Line("drwxr-xr-x", 0, local, "k" + Short, "docs/")));
        Assert.Null(KopiaAdapter.ParseListLine(Line("drwxr-xr-x", 0, local, "k" + Short, "docs/") + " (2 errors)"));
        Assert.Null(KopiaAdapter.ParseListLine(Line("dtrwxrwxrwx", 0, local, "k" + Short, "tmp/")));
        Assert.Null(KopiaAdapter.ParseListLine(Line("Lrwxrwxrwx", 9, local, Short, "docs/link")));
        Assert.Null(KopiaAdapter.ParseListLine("NOTE: Encountered 2 errors while snapshotting this directory:"));
        Assert.Null(KopiaAdapter.ParseListLine(string.Empty));
        Assert.Null(KopiaAdapter.ParseListLine(Line("-rw-r--r--", 11, local, Short, "x")[..60]));
    }

    private static string P(string unix) => OperatingSystem.IsWindows() ? "C:" + unix.Replace('/', '\\') : unix;

    [Fact]
    public void TheSnapshotChosen_HoldsTheWholeSource_ThenWasTakenHere_ThenIsNewest()
    {
        var here = Environment.MachineName.ToLowerInvariant();
        var day = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var part = new KopiaAdapter.Snapshot("a0000000", day.AddDays(4), here, P("/home/max/docs/work"), "ka");
        var parentElsewhere = new KopiaAdapter.Snapshot("b0000000", day.AddDays(3), "desktop", P("/home/max"), "kb");
        var exactHere = new KopiaAdapter.Snapshot("c0000000", day.AddDays(1), here, P("/home/max/docs"), "kc");
        var unrelated = new KopiaAdapter.Snapshot("d0000000", day.AddDays(5), here, P("/srv"), "kd");
        var root = P("/home/max/docs");

        Assert.Equal([new KopiaAdapter.Pick(exactHere, "", "")], KopiaAdapter.Choose([part, parentElsewhere, exactHere, unrelated], root));
        Assert.Equal([new KopiaAdapter.Pick(parentElsewhere, "docs", "")], KopiaAdapter.Choose([part, parentElsewhere, unrelated], root));
        Assert.Equal([new KopiaAdapter.Pick(part, "", "work")], KopiaAdapter.Choose([part, unrelated], root));
        Assert.Empty(KopiaAdapter.Choose([unrelated], root));

        // Folders inside the source with snapshots of their own: the newest of each.
        var olderPart = part with { Id = "e0000000", StartTime = day };
        var photos = new KopiaAdapter.Snapshot("f0000000", day.AddDays(2), here, P("/home/max/docs/photos/2026"), "kf");
        Assert.Equal([new KopiaAdapter.Pick(photos, "", "photos/2026"), new KopiaAdapter.Pick(part, "", "work")],
            KopiaAdapter.Choose([olderPart, photos, part, unrelated], root));
    }

    [Fact]
    public void PathsBelowAFolder_AreFound_AsTheFileSystemComparesNames()
    {
        Assert.Equal("", KopiaAdapter.Below(P("/home/max"), P("/home/max/")));
        Assert.Equal("docs/work", KopiaAdapter.Below(P("/home/max"), P("/home/max/docs/work")));
        Assert.Null(KopiaAdapter.Below(P("/home/max"), P("/home/maxine/docs")));
        Assert.Null(KopiaAdapter.Below(P("/home/max/docs"), P("/home/max")));
        if (OperatingSystem.IsWindows())
        {
            // Typed in lower case when the snapshot was made, kept that way by kopia.
            Assert.Equal("Documents", KopiaAdapter.Below(@"c:\users\max", @"C:\Users\max\Documents"));
            Assert.Equal(@"Users/max", KopiaAdapter.Below(@"C:\", @"C:\Users\max"));
        }
        else
        {
            Assert.Null(KopiaAdapter.Below("/home/MAX", "/home/max/docs"));
            Assert.Equal("home/max", KopiaAdapter.Below("/", "/home/max"));
        }
    }

    [Fact]
    public void KopiasWordsForDamage_AreRecognised()
    {
        Assert.True(KopiaAdapter.IsDamage("unable to copy data: invalid checksum at p0a1b2c3d4e5f offset 0 length 1234/1234: unable to decrypt content: cipher: message authentication failed"));
        Assert.True(KopiaAdapter.IsDamage("error getting cached content: BLOB not found"));
        Assert.True(KopiaAdapter.IsDamage("unable to open object: content not found"));
        Assert.False(KopiaAdapter.IsDamage("cannot access storage path: The network path was not found."));
    }

    [Fact]
    public void KopiasErrors_AreExplainedInWords()
    {
        var destination = Destination.Kopia(P("/srv/kopia"), "secret");
        string Explain(string error, int code = 1) => KopiaAdapter.Explain(destination, new ToolRunner.Result(code, string.Empty, error));

        Assert.Equal("the password was not accepted", Explain("error connecting to repository: invalid repository password"));
        Assert.Equal($"no Kopia repository was found at {P("/srv/kopia")}", Explain("error connecting to repository: repository not initialized in the provided storage"));
        Assert.StartsWith("this connection goes through a Kopia server", Explain("can't connect to storage: connection file does not specify blob storage connection parameters, kopia server connections are not supported"));
        Assert.Equal("kopia said: unable to open repository: boom", Explain("Some warning first\nERROR unable to open repository: boom\n"));
        Assert.Equal("kopia stopped with exit code 3", Explain(string.Empty, 3));
        Assert.Equal("kopia could not be started; install it, or say where it is in Settings", Explain("not found", int.MinValue));
    }

    [Fact]
    public async Task AFolderWithoutARepository_IsUnreachable_AndKopiaIsNeverAskedToOpenIt()
    {
        // kopia, asked to open a folder without a repository, would write a .shards file into it.
        using var source = new TempTree();
        using var empty = new TempTree();
        WriteDocuments(source, 3, LastWeek);

        var result = await DrillOnce(source.Root, Destination.Kopia(empty.Root, KopiaRepository.Password, "Repository", "kopia-that-is-never-run"));

        Assert.False(result.Reachable);
        Assert.Equal($"no Kopia repository was found at {empty.Root}", result.ProbeDetail);
        Assert.Empty(Directory.EnumerateFileSystemEntries(empty.Root));
    }

    [Fact]
    public async Task ConnectionFiles_AreReadForWhereTheRepositoryIs_BeforeKopiaIsAsked()
    {
        using var folder = new TempTree();
        var unplugged = P("/media/unplugged-drive/kopia");
        var adapter = new KopiaAdapter();
        Destination WithConfig(string name, string json) =>
            Destination.Kopia(folder.Write(name, json), KopiaRepository.Password, toolPath: "kopia-that-is-never-run");
        static string Filesystem(string path) => System.Text.Json.JsonSerializer.Serialize(new { storage = new { type = "filesystem", config = new { path } } });

        var server = await adapter.ProbeAsync(WithConfig("server.config", """{"apiServer":{"url":"https://nas:51515"},"hostname":"laptop"}"""), CancellationToken.None);
        Assert.False(server.Reachable);
        Assert.StartsWith("this connection goes through a Kopia server", server.Detail);

        var absent = await adapter.ProbeAsync(WithConfig("drive.config", Filesystem(unplugged)), CancellationToken.None);
        Assert.False(absent.Reachable);
        Assert.Equal($"{unplugged} was not found", absent.Detail);

        var notRepository = await adapter.ProbeAsync(WithConfig("empty.config", Filesystem(folder.Root)), CancellationToken.None);
        Assert.Equal($"no Kopia repository was found at {folder.Root}", notRepository.Detail);

        Assert.Equal((null, false), KopiaAdapter.StorageOf(folder.Write("s3.config", """{"storage":{"type":"s3","config":{"bucket":"b"}}}""")));
        Assert.Equal((null, false), KopiaAdapter.StorageOf(folder.Write("broken.config", "{")));

        var missing = await adapter.ProbeAsync(Destination.Kopia(folder.At("gone/repository.config"), "x"), CancellationToken.None);
        Assert.Equal($"{folder.At("gone/repository.config")} was not found", missing.Detail);
    }

    [Fact]
    public void KopiaDestinations_AreNamedAndDescribed_AndKeepTheirPasswordToThemselves()
    {
        var folder = Destination.Kopia(P("/srv/kopia-repo"), "hunter2");
        Assert.Equal("kopia-repo", folder.Name);
        Assert.Equal(DestinationKind.Kopia, folder.Kind);
        Assert.Equal("Kopia repository at " + P("/srv/kopia-repo"), Destination.Describe(DestinationKind.Kopia, folder.RootPath));
        Assert.DoesNotContain("hunter2", folder.ToString());

        var connection = Destination.Kopia(P("/home/max/.config/kopia/repository.config"), "hunter2");
        Assert.Equal("Kopia", connection.Name);
        Assert.True(Destination.IsKopiaConfig(connection.RootPath));
        Assert.Equal("Kopia repository connected by " + connection.RootPath, Destination.Describe(DestinationKind.Kopia, connection.RootPath));
        Assert.NotEqual(Destination.Restic(P("/srv/kopia-repo"), null).Id, folder.Id);
        Assert.Equal(folder.Id, Destination.Kopia(P("/srv/kopia-repo/"), null).Id);
        Assert.True(HostNames.IsThisPc(Environment.MachineName.ToLowerInvariant()));
    }
}
