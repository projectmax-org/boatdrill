using System.Text.Json;
using Boatdrill.Core.Adapters;
using Boatdrill.Core.Drill;
using Boatdrill.Core.Sampling;
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
        Assert.Equal("None of the backups could be looked in, so nothing was compared.", none.Sentence);

        var some = await ScanAsync(source, gone, Destination.MirrorFolder(mirror.Root, "Drive"));
        Assert.Equal(1, some.OnlyHereFiles);
        Assert.EndsWith("1 backup could not be looked in and may hold copies of some of these files.", some.Sentence);
    }

    [Fact]
    public async Task ABackupWhoseFilesCannotBeListed_IsNotLookedIn_AndDoesNotMakeFilesOnlyHere()
    {
        using var source = new TempTree();
        using var mirror = new TempTree();
        using var scratch = new TempTree();
        source.Write("x.txt", "x", Old);
        var remote = Destination.Rclone("remote:backup", name: "Remote");
        var scanner = new OnlyHereScanner([new MirrorFolderAdapter(), new CannotList()]);

        var alone = await scanner.ScanAsync([Source.FromPath(source.Root, "Docs")], [remote], scratch.Root);
        Assert.Equal(0, alone.OnlyHereFiles);
        Assert.Empty(alone.Checked);
        Assert.Empty(alone.Sources);
        Assert.Equal("Remote: its files could not be listed: the remote said no", Assert.Single(alone.Unreachable));
        Assert.Equal("None of the backups could be looked in, so nothing was compared.", alone.Sentence);

        source.MirrorTo(mirror.Root);
        var beside = await scanner.ScanAsync([Source.FromPath(source.Root, "Docs")], [remote, Destination.MirrorFolder(mirror.Root, "Drive")], scratch.Root);
        Assert.Equal(["Drive"], beside.Checked);
        Assert.Single(beside.Unreachable);
        Assert.Equal(0, beside.OnlyHereFiles);
    }

    [Fact]
    public async Task AFolderNoBackupCouldBeLookedInFor_IsNamed_AndDoesNotPassForCopied()
    {
        using var docs = new TempTree();
        using var photos = new TempTree();
        using var scratch = new TempTree();
        docs.Write("x.txt", "x", Old);
        photos.Write("y.jpg", "y", Old);

        var report = await new OnlyHereScanner([new CannotList(failsFor: "Docs")]).ScanAsync(
            [Source.FromPath(docs.Root, "Docs"), Source.FromPath(photos.Root, "Photos")], [Destination.Rclone("remote:backup", name: "Remote")], scratch.Root);

        Assert.Equal(["Remote"], report.Checked);
        Assert.Equal(["Docs"], report.NotCompared);
        Assert.Equal("Photos", Assert.Single(report.Sources).SourceName);
        Assert.Equal(0, report.OnlyHereFiles);
        Assert.Equal("All 1 file has a current copy in at least one backup. Docs could not be compared with any backup.", report.Sentence);
    }

    [Fact]
    public async Task AProtectedFolderThatIsGone_IsNamed_AndNeverCountedAsCompared()
    {
        using var docs = new TempTree();
        using var elsewhere = new TempTree();
        using var mirror = new TempTree();
        using var scratch = new TempTree();
        docs.Write("x.txt", "x", Old);
        docs.Write("y.txt", "y", Old);
        docs.MirrorTo(mirror.Root);
        var kept = Source.FromPath(docs.Root, "Docs");
        var gone = Source.FromPath(elsewhere.At("Projects"));
        var drive = Destination.MirrorFolder(mirror.Root, "Drive");

        // Renamed, moved, or on a drive that is not plugged in: its files may well have no copy, and none was looked up.
        var report = await new OnlyHereScanner().ScanAsync([kept, gone], [drive], scratch.Root);

        Assert.Equal("Docs", Assert.Single(report.Sources).SourceName);
        var missing = Assert.Single(report.NotFound);
        Assert.Equal(("Projects", gone.RootPath, 0L), (missing.SourceName, missing.RootPath, missing.Files));
        Assert.Empty(report.NotCompared);
        Assert.Equal(0, report.OnlyHereFiles);
        Assert.Equal(
            $"Projects was not found at {gone.RootPath}, so none of its files were looked up. All 2 files that were looked up have a current copy in at least one backup.",
            report.Sentence);
        Assert.EndsWith(
            " All 2 files that were looked up have a current copy in at least one backup. Photos could not be compared with any backup.",
            OnlyHereScanner.Sentence(report with { NotCompared = ["Photos"] }));

        // A file with no copy is still the first thing said; the folder that was not found comes after it.
        docs.Write("z.txt", "z", Old);
        var some = await new OnlyHereScanner().ScanAsync([kept, gone], [drive], scratch.Root);
        Assert.Equal(
            $"1 of 3 files (1 bytes) exist only on this PC: 1 has no copy in any backup. Projects was not found at {gone.RootPath}, so none of its files were looked up.",
            some.Sentence);

        // Alone, it is not a folder with no files in it.
        var alone = await new OnlyHereScanner().ScanAsync([gone], [drive], scratch.Root);
        Assert.Empty(alone.Sources);
        Assert.Single(alone.NotFound);
        Assert.Equal($"Projects was not found at {gone.RootPath}, so none of its files were looked up.", alone.Sentence);

        var both = report with { NotFound = [missing, missing with { SourceName = "Photos" }], Sources = [] };
        Assert.Equal("Projects and Photos were not found, so none of their files were looked up.", OnlyHereScanner.Sentence(both));

        // The page's headline for it is the dashboard's, made from the names a scan keeps.
        Assert.Equal("Projects was not found", Verdicts.MissingHeadline(report.NotFound.Select(s => s.SourceName).ToList()));
        Assert.Equal("2 protected folders were not found", Verdicts.MissingHeadline(both.NotFound.Select(s => s.SourceName).ToList()));

        using (new InKorean())
        {
            Assert.Equal(
                $"Projects 폴더({gone.RootPath})를 찾을 수 없어 살펴보지 못했습니다. 살펴본 파일 2개 모두 적어도 한 백업에 최신 사본이 있습니다.",
                OnlyHereScanner.Sentence(report));
            Assert.Equal(
                $"파일 3개 중 1개(1바이트)가 이 PC에만 있습니다. 1개는 어느 백업에도 사본이 없습니다. Projects 폴더({gone.RootPath})를 찾을 수 없어 살펴보지 못했습니다.",
                OnlyHereScanner.Sentence(some));
            Assert.Equal($"Projects 폴더({gone.RootPath})를 찾을 수 없어 살펴보지 못했습니다.", OnlyHereScanner.Sentence(alone));
            Assert.Equal("Projects, Photos 폴더를 찾을 수 없어 살펴보지 못했습니다.", OnlyHereScanner.Sentence(both));
            Assert.Equal("Projects 폴더를 찾을 수 없습니다", Verdicts.MissingHeadline(report.NotFound.Select(s => s.SourceName).ToList()));
        }
    }

    [Fact]
    public async Task AFolderThatGoesWhileItsBackupIsListed_IsNotFound_NotEmpty()
    {
        using var docs = new TempTree();
        using var mirror = new TempTree();
        using var scratch = new TempTree();
        docs.Write("x.txt", "x", Old);
        docs.MirrorTo(mirror.Root);

        var report = await new OnlyHereScanner([new LosesTheSource(docs.Root)]).ScanAsync(
            [Source.FromPath(docs.Root, "Docs")], [Destination.MirrorFolder(mirror.Root, "Drive")], scratch.Root);

        Assert.Empty(report.Sources);
        Assert.Equal("Docs", Assert.Single(report.NotFound).SourceName);
        Assert.Equal($"Docs was not found at {docs.Root}, so none of its files were looked up.", report.Sentence);
    }

    /// <summary>A folder of copies, during whose listing the protected folder goes away: its drive is unplugged, say.</summary>
    private sealed class LosesTheSource(string sourceRoot) : IDestinationAdapter
    {
        private readonly MirrorFolderAdapter _inner = new();

        public DestinationKind Kind => DestinationKind.MirrorFolder;

        public Task<DestinationProbe> ProbeAsync(Destination destination, CancellationToken ct) => _inner.ProbeAsync(destination, ct);

        public Task<SourceBinding> BindAsync(Destination destination, Sample sample, string scratchDirectory, BindPurpose purpose,
            IReadOnlyCollection<string> alsoRead, CancellationToken ct)
        {
            Directory.Delete(sourceRoot, recursive: true);
            return _inner.BindAsync(destination, sample, scratchDirectory, purpose, alsoRead, ct);
        }

        public Task<CopyLookup> LocateAsync(SourceBinding binding, string relativePath, CancellationToken ct) => _inner.LocateAsync(binding, relativePath, ct);

        public Task<ReadBack> ReadBackAsync(SourceBinding binding, CopyLookup copy, string scratchPath, CancellationToken ct) =>
            _inner.ReadBackAsync(binding, copy, scratchPath, ct);
    }

    [Fact]
    public void AScanSavedBeforeMissingFoldersWereNamed_StillLoads_AndReadsAsBefore()
    {
        const string old = """{"startedUtc":"2026-10-27T04:55:00+00:00","finishedUtc":"2026-10-27T04:57:00+00:00","checked":["Drive"],"unreachable":[],"notCompared":["Photos"],"sources":[{"sourceId":"a","sourceName":"Docs","rootPath":"/home/max/docs","files":2,"bytes":20,"noCopyFiles":0,"noCopyBytes":0,"olderFiles":0,"olderBytes":0}],"folders":[],"largest":[],"sentence":"unused","onlyHereFiles":0,"onlyHereBytes":0}""";

        var report = JsonSerializer.Deserialize<OnlyHereReport>(old, BoatdrillJson.Options)!;

        Assert.Empty(report.NotFound);
        Assert.Equal(2, Assert.Single(report.Sources).Files);
        Assert.Equal("All 2 files have a current copy in at least one backup. Photos could not be compared with any backup.", OnlyHereScanner.Sentence(report));

        // What is written now names the folder, and comes back as it was written.
        var named = report with { NotFound = [new OnlyHereSource("p", "Projects", "/home/max/projects")] };
        var read = JsonSerializer.Deserialize<OnlyHereReport>(JsonSerializer.Serialize(named, BoatdrillJson.Options), BoatdrillJson.Options)!;
        Assert.Equal(("Projects", "/home/max/projects"), (read.NotFound[0].SourceName, read.NotFound[0].RootPath));
    }

    [Fact]
    public async Task OnlineOnlyFiles_AreLeftOutAndCounted_AndAFolderOfNothingElseIsNotCalledEmpty()
    {
        if (!OperatingSystem.IsWindows()) return; // OFFLINE, which stands for every online-only flag, is a Windows attribute.

        using var source = new TempTree();
        using var mirror = new TempTree();
        for (var i = 0; i < 5; i++) source.Write($"docs/{i}.txt", $"document {i}", Old);
        source.MirrorTo(mirror.Root, skip: ["docs/3.txt", "docs/4.txt"]);
        var all = Enumerable.Range(0, 5).Select(i => source.At(Path.Combine("docs", $"{i}.txt"))).ToArray();
        try
        {
            foreach (var file in all[3..]) File.SetAttributes(file, FileAttributes.Offline);
            var some = await ScanAsync(source, Destination.MirrorFolder(mirror.Root, "Drive"));

            // The two that are online only have no copy in this backup, and are not what would be lost with this PC:
            // they are left out, and the report says how many there were.
            var summary = Assert.Single(some.Sources);
            Assert.Equal((3L, 2L, 0L), (summary.Files, summary.OnlineOnlyFiles, some.OnlyHereFiles));
            Assert.Equal("All 3 files have a current copy in at least one backup. 2 online-only files are not on this PC and were left out.", some.Sentence);

            foreach (var file in all) File.SetAttributes(file, FileAttributes.Offline);
            var none = await ScanAsync(source, Destination.MirrorFolder(mirror.Root, "Drive"));

            Assert.Equal((0L, 5L), (Assert.Single(none.Sources).Files, none.OnlineOnlyFiles));
            Assert.Equal("5 online-only files are not on this PC and were left out. There was no other file to look up.", none.Sentence);
            using var korean = new InKorean();
            Assert.Equal("온라인 전용 파일 5개는 이 PC에 없어 살펴보지 않았습니다. 그 밖에 살펴볼 파일은 없었습니다.", OnlyHereScanner.Sentence(none));
        }
        finally
        {
            foreach (var file in all) File.SetAttributes(file, FileAttributes.Normal);
        }
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
