using Boatdrill.Core.Drill;

namespace Boatdrill.Core.Tests;

public sealed class ScratchTests
{
    [Fact]
    public void AScratchFolder_IsDeleted_EvenWithReadOnlyFoldersAndFilesInIt()
    {
        // A restore can bring back Windows' attributes: a C\Users folder comes back read-only, as C:\Users is.
        using var tree = new TempTree();
        var file = tree.Write("scratch/C/Users/max/docs/a.txt", "restored");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        new DirectoryInfo(tree.At("scratch/C/Users")).Attributes |= FileAttributes.ReadOnly;
        new DirectoryInfo(tree.At("scratch/C/Users/max")).Attributes |= FileAttributes.ReadOnly | FileAttributes.Hidden;

        Assert.True(Scratch.TryDelete(tree.At("scratch")));
        Assert.False(Directory.Exists(tree.At("scratch")));
    }

    [Fact]
    public void ALinkInAScratchFolder_IsRemoved_AndWhatItPointsAtIsLeftAsItWas()
    {
        using var tree = new TempTree();
        var mine = tree.Write("documents/report.txt", "the user's own");
        File.SetAttributes(mine, FileAttributes.ReadOnly);
        var theirs = new DirectoryInfo(tree.At("documents"));
        theirs.Attributes |= FileAttributes.ReadOnly;
        tree.Write("scratch/stage/C/Users/a.txt", "restored");
        new DirectoryInfo(tree.At("scratch/stage/C/Users")).Attributes |= FileAttributes.ReadOnly;
        try
        {
            Directory.CreateSymbolicLink(tree.At("scratch/stage/link"), tree.At("documents"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // Windows makes links for administrators and in developer mode only.
        }

        Assert.True(Scratch.TryDelete(tree.At("scratch")));

        Assert.False(Directory.Exists(tree.At("scratch")));
        Assert.Equal("the user's own", File.ReadAllText(mine));
        Assert.True(File.GetAttributes(mine).HasFlag(FileAttributes.ReadOnly));
        theirs.Refresh();
        Assert.True(theirs.Attributes.HasFlag(FileAttributes.ReadOnly));
        File.SetAttributes(mine, FileAttributes.Normal);
        theirs.Attributes = FileAttributes.Normal;
    }

    [Fact]
    public void AFolderLeftByAnEarlierDrill_IsSwept_OnceItIsHalfADayOld()
    {
        using var tree = new TempTree();
        tree.Write("boatdrill-left/C/Users/a.txt", "restored");
        new DirectoryInfo(tree.At("boatdrill-left/C/Users")).Attributes |= FileAttributes.ReadOnly;
        tree.Write("not-ours/b.txt", "someone else's");

        // Made just now, it may belong to a drill that is still running.
        Scratch.Sweep(tree.Root, DateTimeOffset.UtcNow);
        Assert.True(Directory.Exists(tree.At("boatdrill-left")));

        Scratch.Sweep(tree.Root, DateTimeOffset.UtcNow.AddDays(1));
        Assert.False(Directory.Exists(tree.At("boatdrill-left")));
        Assert.True(File.Exists(tree.At("not-ours/b.txt")));
    }
}
