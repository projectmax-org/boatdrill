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
        try
        {
            tree.Write("scratch/stage/C/Users/a.txt", "restored");
            new DirectoryInfo(tree.At("scratch/stage/C/Users")).Attributes |= FileAttributes.ReadOnly;
            Assert.True(TryLink(tree.At("scratch/stage/link"), tree.At("documents")), "No link could be made to test with.");

            Assert.True(Scratch.TryDelete(tree.At("scratch")));

            Assert.False(Directory.Exists(tree.At("scratch")));
            Assert.Equal("the user's own", File.ReadAllText(mine));
            Assert.True(File.GetAttributes(mine).HasFlag(FileAttributes.ReadOnly));
            theirs.Refresh();
            Assert.True(theirs.Attributes.HasFlag(FileAttributes.ReadOnly));
        }
        finally
        {
            // Writable again, so that the temp tree can be deleted whatever happened above.
            File.SetAttributes(mine, FileAttributes.Normal);
            theirs.Attributes = FileAttributes.Normal;
            Scratch.TryDelete(tree.At("scratch"));
        }
    }

    /// <summary>
    /// A link to a folder. On Windows a junction, which anyone may make, as a restore can bring one back; a symbolic
    /// link there needs an administrator or developer mode.
    /// </summary>
    private static bool TryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }

        var mklink = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // Backslashes only: mklink reads "/stage" in "scratch/stage/link" as a switch.
        foreach (var argument in new[] { "/c", "mklink", "/J", Path.GetFullPath(link), Path.GetFullPath(target) }) mklink.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(mklink)!;
        process.WaitForExit();
        return process.ExitCode == 0 && new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint);
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
