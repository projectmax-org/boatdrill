using Boatdrill.Core.Adapters;
using Boatdrill.Core.Drill;
using Boatdrill.Core.History;
using Boatdrill.Core.Scan;
using Boatdrill.Core.Sampling;
using Boatdrill.Core.Scoring;
using Boatdrill.Core.Verification;

namespace Boatdrill.Core.Tests;

public sealed class ResticAdapterTests
{
    internal static readonly SampleOptions Everything = new() { SampleSize = 1000, Seed = 1, ChurnWindow = TimeSpan.FromHours(-1), PerFolderFloor = 0 };

    internal static readonly DateTimeOffset LastWeek = DateTimeOffset.UtcNow.AddDays(-7);

    internal static async Task<DestinationResult> DrillOnce(string sourceRoot, Destination destination, HashLedger? ledger = null, TimeProvider? clock = null,
        string[]? excludes = null, bool canaries = false)
    {
        using var scratch = new TempTree();
        var report = await new DrillRunner(clock: clock, ledger: ledger).RunAsync(new DrillPlan
        {
            Sources = [Source.FromPath(sourceRoot, "Docs") with { Excludes = excludes ?? [] }],
            Destinations = [destination],
            ScratchDirectory = scratch.Root,
            Sampling = Everything,
            UseCanaries = canaries,
        });
        return Assert.Single(report.Destinations);
    }

    internal static void WriteDocuments(TempTree tree, int count, DateTimeOffset when)
    {
        for (var i = 0; i < count; i++) tree.Write($"docs/{i}.txt", $"document {i}", when);
    }

    [ResticFact]
    public async Task ASnapshot_Verifies_ByRestoringTheSample()
    {
        using var source = new TempTree();
        using var repository = new ResticRepository();
        WriteDocuments(source, 12, LastWeek);
        source.Write("photos/2026/beach.raw", RandomBytes(300_000), LastWeek);
        repository.Backup(source.Root);

        var result = await DrillOnce(source.Root, repository.Destination());

        Assert.True(result.Reachable, result.ProbeDetail);
        Assert.Equal(DestinationKind.Restic, result.Kind);
        Assert.Equal("Repository holds 1 snapshot", result.ProbeDetail?.Replace(repository.Location, "Repository"));
        Assert.Equal(13, result.Verified);
        Assert.Empty(result.Findings);
        Assert.Equal(Verdict.WouldRestore, result.Score.Verdict);
        var binding = Assert.Single(result.Bindings);
        Assert.Equal(13, binding.ProbeHits);
        Assert.Contains(", snapshot ", binding.BasePath);
        Assert.True(result.BytesRead >= 300_000);
        Assert.NotNull(result.EstimatedFullRestore);
    }

    [ResticFact]
    public async Task ChangesAfterTheSnapshot_AreBehind_AndExcludedFilesAreMissing()
    {
        using var source = new TempTree();
        using var repository = new ResticRepository();
        WriteDocuments(source, 8, LastWeek);
        repository.Backup(source.Root, "--exclude", "7.txt");
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

    [ResticFact]
    public async Task TheNewestSnapshotThatHoldsTheSource_IsDrilled()
    {
        using var source = new TempTree();
        using var other = new TempTree();
        using var repository = new ResticRepository();
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

    [ResticFact]
    public async Task ASnapshotOfAParentFolder_HoldsTheSource()
    {
        using var tree = new TempTree();
        using var repository = new ResticRepository();
        WriteDocuments(tree, 5, LastWeek);
        tree.Write("music/song.txt", "not part of the source", LastWeek);
        repository.Backup(tree.Root);

        var result = await DrillOnce(tree.At("docs"), repository.Destination());

        Assert.Equal(5, result.Sampled);
        Assert.Equal(5, result.Verified);
    }

    [ResticFact]
    public async Task NoSnapshotHoldsTheSource_SaysSo()
    {
        using var source = new TempTree();
        using var other = new TempTree();
        using var repository = new ResticRepository();
        WriteDocuments(source, 4, LastWeek);
        other.Write("elsewhere.txt", "x", LastWeek);
        repository.Backup(other.Root);

        var result = await DrillOnce(source.Root, repository.Destination());

        Assert.True(result.Reachable);
        Assert.Equal(4, result.Missing);
        Assert.Equal(Verdict.WouldNotRestore, result.Score.Verdict);
        Assert.Contains($"Docs: no snapshot in the repository holds {source.Root}.", result.Sentence);
    }

    [ResticFact]
    public async Task CopiesVerifiedBefore_AreRestoredAgain_AndANewVersionIsNotRot()
    {
        using var source = new TempTree();
        using var repository = new ResticRepository();
        WriteDocuments(source, 6, LastWeek);
        repository.Backup(source.Root);
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var ledger = new HashLedger();
        Assert.Equal(6, (await DrillOnce(source.Root, repository.Destination(), ledger, clock)).Verified);

        // The same size and date, other bytes, backed up again: restic stored a new version, which is not decay.
        source.Write("docs/2.txt", "DOCUMENT 2", LastWeek);
        repository.Backup(source.Root, "--force");
        clock.Now = clock.Now.AddDays(2);
        var second = await DrillOnce(source.Root, repository.Destination(), ledger, clock, excludes: ["1.txt", "2.txt"]);

        Assert.Equal(4, second.Verified);
        Assert.Equal(1, second.Rechecked);
        Assert.Equal(0, second.Rotted);
        Assert.Empty(second.Findings);
        Assert.DoesNotContain(ledger.Entries, e => e.RelativePath.EndsWith("2.txt", StringComparison.Ordinal));
        Assert.Contains(ledger.Entries, e => e.RelativePath.EndsWith("1.txt", StringComparison.Ordinal) && e.VerifiedUtc == clock.Now);
    }

    [ResticFact]
    public async Task TheCanary_IsRestoredWithTheSample_AndShowsTheMissedStamp()
    {
        using var source = new TempTree();
        using var repository = new ResticRepository();
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

    [ResticFact]
    public async Task AScan_LooksFilesUpInTheSnapshotsIndex()
    {
        using var source = new TempTree();
        using var repository = new ResticRepository();
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
        Assert.Empty(Directory.EnumerateFileSystemEntries(scratch.Root, "*", SearchOption.AllDirectories).Where(File.Exists));
    }

    [ResticFact]
    public async Task NamesResticReadsAsPatterns_AreFetchedOneByOne()
    {
        using var source = new TempTree();
        using var repository = new ResticRepository();
        source.Write("docs/report [final].txt", "brackets", LastWeek);
        source.Write("docs/plain.txt", "plain", LastWeek);
        if (!OperatingSystem.IsWindows())
        {
            source.Write("docs/plan*.txt", "star", LastWeek);
            source.Write("docs/why?.txt", "question mark", LastWeek);
            source.Write("docs/back\\slash.txt", "backslash", LastWeek);
        }

        repository.Backup(source.Root);

        var result = await DrillOnce(source.Root, repository.Destination());

        Assert.Equal(OperatingSystem.IsWindows() ? 2 : 5, result.Verified);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void SnapshotsAndNodes_Parse_WithGosNanoseconds()
    {
        var snapshot = Assert.Single(ResticAdapter.ParseSnapshots("""
            [{"time":"2026-10-19T21:00:00.123456789+09:00","tree":"ab","paths":["/home/max/docs"],"hostname":"laptop",
              "id":"0123456789abcdef0123456789abcdef","short_id":"01234567"}]
            """));
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero).AddTicks(1_234_567), snapshot.Time);
        Assert.Equal("01234567", snapshot.ShortId);
        Assert.Equal(["/home/max/docs"], snapshot.Paths);

        var node = ResticAdapter.ParseNode("""{"name":"a.txt","type":"file","path":"/home/max/docs/a.txt","size":5,"mtime":"2026-10-19T12:00:00.5Z","struct_type":"node"}""");
        Assert.Equal(new ResticAdapter.Node("/home/max/docs/a.txt", 5, new DateTimeOffset(2026, 10, 19, 12, 0, 0, 500, TimeSpan.Zero)), node);
        Assert.Null(ResticAdapter.ParseNode("""{"time":"2026-10-19T12:00:00Z","tree":"ab","paths":["/home"],"struct_type":"snapshot"}"""));
        Assert.Null(ResticAdapter.ParseNode("""{"name":"docs","type":"dir","path":"/home/max/docs","struct_type":"node"}"""));
        Assert.Null(ResticAdapter.ParseNode("not json"));
        Assert.Empty(ResticAdapter.ParseSnapshots("[]"));
    }

    [Fact]
    public void TheSnapshotChosen_HoldsTheWholeSource_ThenWasTakenHere_ThenIsNewest()
    {
        static string P(string unix) => OperatingSystem.IsWindows() ? "C:" + unix.Replace('/', '\\') : unix;
        var here = Environment.MachineName;
        var day = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var part = new ResticAdapter.Snapshot("a0000000", "a0000000", day.AddDays(4), [P("/home/max/docs/work")], here);
        var parentElsewhere = new ResticAdapter.Snapshot("b0000000", "b0000000", day.AddDays(3), [P("/home/max")], "desktop");
        var exactHere = new ResticAdapter.Snapshot("c0000000", "c0000000", day.AddDays(1), [P("/home/max/docs")], here);
        var unrelated = new ResticAdapter.Snapshot("d0000000", "d0000000", day.AddDays(5), [P("/srv")], here);
        var root = ResticAdapter.TreePath(P("/home/max/docs"));

        var chosen = ResticAdapter.Choose([part, parentElsewhere, exactHere, unrelated], root);
        Assert.Equal("c0000000", chosen?.Snapshot.Id);
        Assert.Equal([root], chosen?.Roots);

        chosen = ResticAdapter.Choose([part, parentElsewhere, unrelated], root);
        Assert.Equal("b0000000", chosen?.Snapshot.Id);
        Assert.Equal([root], chosen?.Roots);

        chosen = ResticAdapter.Choose([part, unrelated], root);
        Assert.Equal("a0000000", chosen?.Snapshot.Id);
        Assert.Equal([ResticAdapter.TreePath(P("/home/max/docs/work"))], chosen?.Roots);

        Assert.Null(ResticAdapter.Choose([unrelated], root));
    }

    [Fact]
    public void FoldersAreListed_AsTheSnapshotSpellsThem()
    {
        if (OperatingSystem.IsWindows())
        {
            // Typed in lower case at backup time, kept that way by restic, matched exactly by its ls.
            var typed = new ResticAdapter.Snapshot("e0000000", "e0000000", DateTimeOffset.UtcNow, [@"c:\users\max"], "laptop");
            var (rank, roots) = ResticAdapter.Coverage(typed, ResticAdapter.TreePath(@"C:\Users\max\Documents"));
            Assert.Equal(2, rank);
            Assert.Equal(["/c/users/max/Documents"], roots);
            Assert.Equal("/C/Users/max", ResticAdapter.TreePath(@"C:\Users\max\"));
            Assert.Equal("/D", ResticAdapter.TreePath(@"D:\"));
        }
        else
        {
            var typed = new ResticAdapter.Snapshot("e0000000", "e0000000", DateTimeOffset.UtcNow, ["/home/MAX"], "laptop");
            Assert.Equal(0, ResticAdapter.Coverage(typed, "/home/max/docs").Rank);
            Assert.Equal("/home/max", ResticAdapter.TreePath("/home/max/"));
            Assert.Equal("/", ResticAdapter.TreePath("/"));
        }
    }

    [Fact]
    public void HostNames_MatchThisPc_InGosAndDotnetsForms()
    {
        var here = Environment.MachineName;

        Assert.True(ResticAdapter.TakenHere(here.ToLowerInvariant()));
        Assert.True(ResticAdapter.TakenHere(here + ".local"));
        Assert.False(ResticAdapter.TakenHere(here + "-other"));
    }

    [Fact]
    public void PasswordsInRepositoryUrls_AreMasked_WhereverTheyAreShown()
    {
        Assert.Equal("rest:https://max:***@nas:8000/restic", Destination.Redact("rest:https://max:hunter2@nas:8000/restic"));
        Assert.Equal("sftp:max@nas:/srv/restic", Destination.Redact("sftp:max@nas:/srv/restic"));

        var destination = Destination.Restic("rest:https://max:hunter2@nas:8000/restic", "repository-password");
        Assert.Equal("restic", destination.Name);
        Assert.DoesNotContain("hunter2", destination.ToString());
        Assert.DoesNotContain("repository-password", destination.ToString());
        Assert.Equal("rest:https://max:***@nas:8000/restic", destination.DisplayLocation);
    }

    [Fact]
    public void ResticsWordsForDamage_AreRecognised()
    {
        Assert.True(ResticAdapter.IsDamage("ciphertext verification failed"));
        Assert.True(ResticAdapter.IsDamage("Fatal: cannot dump file: decrypting blob 2104f96f failed: ciphertext verification failed"));
        Assert.False(ResticAdapter.IsDamage("StreamPack: open /srv/restic/data/da/da05f647: no such file or directory"));
    }

    internal static byte[] RandomBytes(int count)
    {
        var bytes = new byte[count];
        new Random(7).NextBytes(bytes);
        return bytes;
    }
}
