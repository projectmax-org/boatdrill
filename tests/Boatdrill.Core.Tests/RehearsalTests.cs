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
