using Boatdrill.Core.Drill;
using Boatdrill.Core.Verification;

namespace Boatdrill.Core.Tests;

public sealed class RehearsalTests
{
    private static readonly DateTimeOffset Old = DateTimeOffset.UtcNow.AddDays(-20);

    private static Task<RehearsalResult> RehearseAsync(TempTree source, Destination destination, TempTree scratch, string folder = "",
        long batchBytes = RehearsalRunner.DefaultBatchBytes, Func<string, long?>? freeSpace = null) =>
        new RehearsalRunner(freeSpace: freeSpace).RunAsync(new RehearsalPlan(Source.FromPath(source.Root, "Docs"), destination, scratch.Root)
        {
            Folder = folder,
            BatchBytes = batchBytes,
        });

    [Fact]
    public async Task ACompleteMirror_RestoresEveryFile_Intact()
    {
        using var source = new TempTree();
        using var mirror = new TempTree();
        using var scratch = new TempTree();
        for (var i = 0; i < 30; i++) source.Write($"folder{i % 3}/{i}.txt", new string('x', 100 + i), Old);
        source.MirrorTo(mirror.Root);

        var result = await RehearseAsync(source, Destination.MirrorFolder(mirror.Root, "Drive"), scratch);

        Assert.True(result.AllIntact);
        Assert.Equal(30, result.Files);
        Assert.Equal(30, result.Verified);
        Assert.Equal(result.Bytes, result.BytesRestored);
        Assert.Empty(result.Findings);
        Assert.StartsWith("All 30 files in Docs came back from Drive intact. Restoring ", result.Sentence);
        TempTree.AssertEmpty(scratch.Root);
    }

    [Fact]
    public async Task OnlineOnlyFiles_AreNotDownloadedToCompare_AndTheSentenceSaysSo()
    {
        if (!OperatingSystem.IsWindows()) return; // OFFLINE, which stands for every online-only flag, is a Windows attribute.

        using var source = new TempTree();
        using var mirror = new TempTree();
        using var scratch = new TempTree();
        for (var i = 0; i < 5; i++) source.Write($"docs/{i}.txt", $"document {i}", Old);
        source.MirrorTo(mirror.Root);
        var away = source.At(Path.Combine("docs", "4.txt"));
        File.SetAttributes(away, FileAttributes.Offline);
        try
        {
            var result = await RehearseAsync(source, Destination.MirrorFolder(mirror.Root, "Drive"), scratch);

            Assert.True(result.AllIntact);
            Assert.Equal((4L, 4L, 1L), (result.Files, result.Verified, result.OnlineOnly));
            Assert.EndsWith(" 1 online-only file was not compared: its bytes are not on this PC.", result.Sentence);
            Assert.Equal(("All 4 files in Docs that are on this PC came back intact", "fair"), RehearsalRunner.Headline(result));

            // With every file online only, nothing is restored, and nothing is shown: not a pass, nor an empty folder.
            foreach (var file in Directory.GetFiles(source.At("docs"))) File.SetAttributes(file, FileAttributes.Offline);
            var nothingHere = await RehearseAsync(source, Destination.MirrorFolder(mirror.Root, "Drive"), scratch);

            Assert.False(nothingHere.AllIntact);
            Assert.Equal((0L, 0L, 5L), (nothingHere.Files, nothingHere.Verified, nothingHere.OnlineOnly));
            Assert.Equal("Docs has no files on this PC to restore. 5 online-only files were not compared: their bytes are not on this PC.", nothingHere.Sentence);
            Assert.Equal(("Docs has no files on this PC to restore", "none"), RehearsalRunner.Headline(nothingHere));
        }
        finally
        {
            foreach (var file in Directory.GetFiles(source.At("docs"))) File.SetAttributes(file, FileAttributes.Normal);
        }
    }

    [Fact]
    public void AFolderThatIsAllOnline_IsNotCalledEmpty()
    {
        var result = new RehearsalResult
        {
            SourceName = "사진",
            SourceRoot = @"C:\Users\you\OneDrive\사진",
            DestinationId = "drive",
            DestinationName = "Drive",
            RootPath = @"E:\Backup",
            StartedUtc = Old,
            FinishedUtc = Old,
            Reachable = true,
            OnlineOnly = 3,
            Sentence = string.Empty,
        };

        Assert.Equal("사진 has no files on this PC to restore. 3 online-only files were not compared: their bytes are not on this PC.", RehearsalRunner.Sentence(result));
        using var korean = new InKorean();
        Assert.Equal("사진에는 이 PC에 있는 파일 중 복원할 것이 없습니다. 온라인 전용 파일 3개는 내용이 이 PC에 없어 비교하지 않았습니다.", RehearsalRunner.Sentence(result));
    }

    [Fact]
    public void OnlineOnlyFilesLeftOut_DoNotFailARehearsal_ButItsHeadlineIsOnlyAboutTheFilesOnThisPc()
    {
        // Documents moved into OneDrive with Files On-Demand: 120 of its files are on this PC, and 880 are online only.
        var whole = Rehearsed(files: 120, verified: 120);
        var partly = whole with { OnlineOnly = 880 };
        var one = Rehearsed(files: 1, verified: 1) with { OnlineOnly = 880 };
        var none = Rehearsed(files: 0, verified: 0) with { OnlineOnly = 880 };

        // What boatdrill-cli rehearse exits 0 for: every file that is on this PC intact. The online-only files are left
        // out and counted, as a drill leaves them out; with nothing on this PC to restore, nothing was shown.
        Assert.True(whole.AllIntact);
        Assert.True(partly.AllIntact);
        Assert.True(one.AllIntact);
        Assert.False(none.AllIntact);
        Assert.False((partly with { Verified = 119, Missing = 1 }).AllIntact);
        Assert.EndsWith(" 880 online-only files were not compared: their bytes are not on this PC.", RehearsalRunner.Sentence(partly));

        // "All of Docs", in the good colour, is only for a rehearsal that left none of the folder's files out.
        Assert.Equal(("All of Docs came back intact", "good"), RehearsalRunner.Headline(whole));
        Assert.Equal(("All 120 files in Docs that are on this PC came back intact", "fair"), RehearsalRunner.Headline(partly));
        Assert.Equal(("The one file in Docs that is on this PC came back intact", "fair"), RehearsalRunner.Headline(one));

        // And a folder whose files are all online only is not one with no files.
        Assert.Equal(("Docs has no files on this PC to restore", "none"), RehearsalRunner.Headline(none));

        using var korean = new InKorean();
        Assert.Equal(("Docs 전체를 온전히 복원했습니다", "good"), RehearsalRunner.Headline(whole));
        Assert.Equal(("Docs의 파일 중 이 PC에 있는 120개를 모두 온전히 복원했습니다", "fair"), RehearsalRunner.Headline(partly));
        Assert.Equal(("Docs의 파일 중 이 PC에 있는 1개를 온전히 복원했습니다", "fair"), RehearsalRunner.Headline(one));
        Assert.Equal(("Docs에는 이 PC에 있는 파일 중 복원할 것이 없습니다", "none"), RehearsalRunner.Headline(none));
    }

    [Fact]
    public void TheHeadline_SaysHowTheRehearsalWent_AndIsColouredToMatch()
    {
        var intact = Rehearsed(files: 400, verified: 400);
        var lost = Rehearsed(files: 400, verified: 397) with { Missing = 2, Outdated = 1 };
        var older = Rehearsed(files: 400, verified: 399) with { Outdated = 1 };
        var empty = Rehearsed(files: 0, verified: 0);
        var refused = empty with { Refusal = "There is no folder 2027 in Docs, so nothing was restored." };
        var unplugged = empty with { Reachable = false, ProbeDetail = "the drive was not found" };

        Assert.Equal(("All of Docs came back intact", "good"), RehearsalRunner.Headline(intact));
        Assert.Equal(($"All of Docs{Path.DirectorySeparatorChar}2026 came back intact", "good"), RehearsalRunner.Headline(intact with { Folder = "2026" }));
        Assert.Equal(("3 of 400 files in Docs did not come back intact", "bad"), RehearsalRunner.Headline(lost));
        Assert.Equal(("1 of 400 files in Docs came back older", "fair"), RehearsalRunner.Headline(older));
        Assert.Equal(("Docs has no files to restore", "none"), RehearsalRunner.Headline(empty));
        Assert.Equal(("The rehearsal did not start", "fair"), RehearsalRunner.Headline(refused));
        Assert.Equal(("Drive could not be reached", "bad"), RehearsalRunner.Headline(unplugged));

        // Files that did not come back are bad news with online-only files left out too.
        Assert.Equal(("3 of 400 files in Docs did not come back intact", "bad"), RehearsalRunner.Headline(lost with { OnlineOnly = 5 }));

        using var korean = new InKorean();
        Assert.Equal(("Docs 전체를 온전히 복원했습니다", "good"), RehearsalRunner.Headline(intact));
        Assert.Equal(("Docs의 파일 400개 중 3개를 온전히 복원하지 못했습니다", "bad"), RehearsalRunner.Headline(lost));
        Assert.Equal(("Docs의 파일 400개 중 1개가 이전 버전으로 복원되었습니다", "fair"), RehearsalRunner.Headline(older));
        Assert.Equal(("Docs에는 복원할 파일이 없습니다", "none"), RehearsalRunner.Headline(empty));
        Assert.Equal(("리허설을 시작하지 못했습니다", "fair"), RehearsalRunner.Headline(refused));
        Assert.Equal(("Drive에 연결할 수 없습니다", "bad"), RehearsalRunner.Headline(unplugged));
    }

    /// <summary>A rehearsal's result as it is kept, for the words that are written again from its numbers.</summary>
    private static RehearsalResult Rehearsed(long files, long verified) => new()
    {
        SourceName = "Docs",
        SourceRoot = "/home/you/Docs",
        DestinationId = "drive",
        DestinationName = "Drive",
        RootPath = "/mnt/backup",
        StartedUtc = Old,
        FinishedUtc = Old,
        Reachable = true,
        Files = files,
        Verified = verified,
        Sentence = string.Empty,
    };

    [Fact]
    public async Task EveryFileIsAccountedFor_NotASample()
    {
        using var source = new TempTree();
        using var mirror = new TempTree();
        using var scratch = new TempTree();
        for (var i = 0; i < 20; i++) source.Write($"docs/{i}.txt", $"document {i}", Old);
        source.MirrorTo(mirror.Root, skip: ["docs/1.txt", "docs/2.txt"]);
        source.Write("docs/3.txt", "document 3, edited", DateTimeOffset.UtcNow.AddHours(-1));
        File.WriteAllText(mirror.At("docs/4.txt"), "DOCUMENT 4");
        File.SetLastWriteTimeUtc(mirror.At("docs/4.txt"), Old.UtcDateTime);

        var result = await RehearseAsync(source, Destination.MirrorFolder(mirror.Root, "Drive"), scratch);

        Assert.False(result.AllIntact);
        Assert.Equal((20L, 16L, 2L, 1L, 1L), (result.Files, result.Verified, result.Missing, result.Outdated, result.Corrupt));
        Assert.Equal(FileOutcome.Corrupt, result.Findings[0].Outcome);
        Assert.Equal(4, result.Findings.Count);
        Assert.StartsWith("16 of 20 files in Docs came back from Drive intact; 2 have no copy, 1 is older than the original and 1 differs", result.Sentence);
    }

    [Fact]
    public async Task AFolderInside_IsRehearsedAlone()
    {
        using var source = new TempTree();
        using var mirror = new TempTree();
        using var scratch = new TempTree();
        for (var i = 0; i < 6; i++) source.Write($"photos/2026/{i}.jpg", $"photo {i}", Old);
        for (var i = 0; i < 4; i++) source.Write($"letters/{i}.txt", $"letter {i}", Old);
        source.MirrorTo(mirror.Root);

        var result = await RehearseAsync(source, Destination.MirrorFolder(mirror.Root, "Drive"), scratch, folder: Path.Combine("photos", "2026"));

        Assert.Equal(6, result.Files);
        Assert.Equal(6, result.Verified);
        Assert.Contains($"in Docs{Path.DirectorySeparatorChar}photos{Path.DirectorySeparatorChar}2026 came back", result.Sentence);
    }

    [Fact]
    public async Task AFolderOutsideTheSource_OrNotThere_IsRefused_AndNothingPassesForIntact()
    {
        using var source = new TempTree();
        using var elsewhere = new TempTree();
        using var mirror = new TempTree();
        using var scratch = new TempTree();
        source.Write("letters/a.txt", "a", Old);
        Directory.CreateDirectory(source.At("empty"));
        elsewhere.Write("b.txt", "b", Old);
        source.MirrorTo(mirror.Root);
        var drive = Destination.MirrorFolder(mirror.Root, "Drive");

        // Its files would be looked up as if they were the source's, and compared with themselves.
        var outside = await RehearseAsync(source, drive, scratch, folder: elsewhere.Root);
        Assert.Equal((0L, false), (outside.Files, outside.AllIntact));
        Assert.Equal($"{elsewhere.Root} is not a folder inside Docs, so nothing was restored.", outside.Sentence);

        var upward = await RehearseAsync(source, drive, scratch, folder: Path.Combine("..", Path.GetFileName(elsewhere.Root)));
        Assert.Equal((0L, false), (upward.Files, upward.AllIntact));
        Assert.NotNull(upward.Refusal);

        var mistyped = await RehearseAsync(source, drive, scratch, folder: "leters");
        Assert.False(mistyped.AllIntact);
        Assert.Equal("There is no folder leters in Docs, so nothing was restored.", mistyped.Sentence);

        var empty = await RehearseAsync(source, drive, scratch, folder: "empty");
        Assert.Null(empty.Refusal);
        Assert.Equal((0L, false), (empty.Files, empty.AllIntact));
        TempTree.AssertEmpty(scratch.Root);

        // In Korean the folder is called a folder once, even when its name says so already.
        using (new InKorean())
        {
            Assert.Equal("Docs에 leters 폴더가 없어 아무것도 복원하지 않았습니다.", (await RehearseAsync(source, drive, scratch, folder: "leters")).Sentence);
            Assert.Equal("Docs에 새 폴더가 없어 아무것도 복원하지 않았습니다.", (await RehearseAsync(source, drive, scratch, folder: "새 폴더")).Sentence);
        }
    }

    [Fact]
    public async Task AProtectedFolderThatIsGone_IsNotFound_NotAFolderWithNoFiles()
    {
        using var elsewhere = new TempTree();
        using var mirror = new TempTree();
        using var scratch = new TempTree();
        mirror.Write("a.txt", "a", Old);
        var gone = Source.FromPath(elsewhere.At("Pictures"));
        var drive = Destination.MirrorFolder(mirror.Root, "Drive");

        var whole = await new RehearsalRunner().RunAsync(new RehearsalPlan(gone, drive, scratch.Root));
        Assert.Equal((0L, false), (whole.Files, whole.AllIntact));
        Assert.Equal($"Pictures was not found at {gone.RootPath}, so nothing was restored.", whole.Sentence);

        // A folder inside it is not there for the same reason, which is the one to give.
        var inside = await new RehearsalRunner().RunAsync(new RehearsalPlan(gone, drive, scratch.Root) { Folder = "2026" });
        Assert.Equal(whole.Sentence, inside.Sentence);
        TempTree.AssertEmpty(scratch.Root);

        using (new InKorean())
        {
            Assert.Equal($"Pictures 폴더({gone.RootPath})를 찾을 수 없어 아무것도 복원하지 않았습니다.",
                (await new RehearsalRunner().RunAsync(new RehearsalPlan(gone, drive, scratch.Root))).Sentence);
        }
    }

    [Fact]
    public async Task ABackupWhoseFilesCannotBeListed_RestoresNothing_AndSaysWhy()
    {
        using var source = new TempTree();
        using var scratch = new TempTree();
        source.Write("a.txt", "a", Old);

        var result = await new RehearsalRunner([new CannotList()]).RunAsync(
            new RehearsalPlan(Source.FromPath(source.Root, "Docs"), Destination.Rclone("remote:backup", name: "Remote"), scratch.Root));

        Assert.False(result.AllIntact);
        Assert.Equal((0L, 0L, 0L), (result.Verified, result.Missing, result.Errors));
        Assert.Equal($"Nothing was restored from Remote: {CannotList.Problem}.", result.Sentence);
        TempTree.AssertEmpty(scratch.Root);
    }

    [Fact]
    public async Task AnUnreachableBackup_OrTooLittleSpace_StopsBeforeReadingAnything()
    {
        using var source = new TempTree();
        using var mirror = new TempTree();
        using var scratch = new TempTree();
        source.Write("a.txt", "a", Old);
        source.MirrorTo(mirror.Root);

        var gone = await RehearseAsync(source, Destination.MirrorFolder(mirror.At("unplugged"), "USB stick"), scratch);
        Assert.False(gone.Reachable);
        Assert.Equal($"USB stick could not be reached: {mirror.At("unplugged")} was not found.", gone.Sentence);

        var cramped = await RehearseAsync(source, Destination.MirrorFolder(mirror.Root, "Drive"), scratch, freeSpace: _ => 1024);
        Assert.NotNull(cramped.Refusal);
        Assert.Equal(0, cramped.Verified);
        Assert.StartsWith("The rehearsal needs ", cramped.Sentence);
    }

    [ResticFact]
    public async Task ARepository_IsRestoredABatchAtATime()
    {
        using var source = new TempTree();
        using var repository = new ResticRepository();
        using var scratch = new TempTree();
        for (var i = 0; i < 12; i++) source.Write($"docs/{i:00}.txt", new string('r', 40), Old);
        source.Write("docs/report [final].txt", "fetched on its own", Old);
        repository.Backup(source.Root);
        source.Write("docs/new.txt", "made after the snapshot", Old.AddDays(1));

        var result = await RehearseAsync(source, repository.Destination(), scratch, batchBytes: 130);

        Assert.Equal(14, result.Files);
        Assert.Equal(13, result.Verified);
        Assert.Equal(1, result.Missing);
        Assert.Equal("docs/new.txt", Assert.Single(result.Findings).RelativePath.Replace('\\', '/'));
        TempTree.AssertEmpty(scratch.Root);
    }
}
