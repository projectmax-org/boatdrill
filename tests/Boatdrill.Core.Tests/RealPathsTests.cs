namespace Boatdrill.Core.Tests;

public sealed class RealPathsTests
{
    [Fact]
    public void ALinkAnywhereInAPath_IsFollowed_ToWhereTheFolderReallyIs()
    {
        using var tree = new TempTree();
        var file = tree.Write("home/Documents/letters/a.txt", "a");
        Assert.True(ScratchTests.TryLink(tree.At("link"), tree.At("home")), "No link could be made to test with.");
        Assert.True(ScratchTests.TryLink(tree.At("link to a link"), tree.At("link")), "No link could be made to test with.");
        var real = new RealPaths();

        // What is not a link is where it says it is; the temp folder itself may be reached through one, as on macOS.
        var home = real.Of(tree.At("home"));
        Assert.Equal(home, real.Of(tree.At("home") + Path.DirectorySeparatorChar));
        Assert.Equal(Path.Combine(home, "Documents", "letters"), real.Of(Path.GetDirectoryName(file)!));

        Assert.Equal(home, real.Of(tree.At("link")));
        Assert.Equal(home, real.Of(tree.At("link to a link")));
        Assert.Equal(Path.Combine(home, "Documents", "letters"), real.Of(tree.At(Path.Combine("link", "Documents", "letters"))));

        // What is not there yet is below where its folder really is.
        Assert.Equal(Path.Combine(home, "Pictures", "2026"), real.Of(tree.At(Path.Combine("link", "Pictures", "2026"))));

        Assert.True(real.Same(tree.At(Path.Combine("link", "Documents")), tree.At(Path.Combine("home", "Documents"))));
        Assert.False(real.Same(tree.At(Path.Combine("link", "Documents")), tree.At("home")));

        // A file is where its folder really is, whether or not it is asked if it is a link itself.
        var letter = Path.Combine(home, "Documents", "letters", "a.txt");
        Assert.Equal(letter, real.OfFile(file, isLink: false));
        Assert.Equal(letter, real.OfFile(tree.At(Path.Combine("link to a link", "Documents", "letters", "a.txt")), isLink: false));
        Assert.Equal(letter, real.OfFile(tree.At(Path.Combine("link", "Documents", "letters", "a.txt")), isLink: true));

        // Asked again, or asked afresh, the answer is the same.
        Assert.Equal(home, real.Of(tree.At("link")));
        Assert.Equal(home, new RealPaths().Of(tree.At("link to a link")));
    }

    [Fact]
    public void ADriveOrAShare_AndAPathThatIsNotThere_AreAsTheyAreSpelled()
    {
        using var tree = new TempTree();
        var root = Path.GetPathRoot(tree.Root)!;
        var real = new RealPaths();

        Assert.Equal(root, real.Of(root));
        Assert.Equal(real.Of(tree.Root), real.Of(Path.Combine(tree.Root, "one", "..")));
        Assert.Equal(Path.Combine(real.Of(tree.Root), "not", "there"), real.Of(tree.At(Path.Combine("not", "there"))));
        Assert.Equal(Path.Combine(real.Of(tree.Root), "not", "there.txt"), real.OfFile(tree.At(Path.Combine("not", "there.txt")), isLink: true));
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(@"\\server\share", real.Of(@"\\server\share\"));
        }
    }

    [Fact]
    public void LinksThatLeadRoundInACircle_AreGivenUp_NotFollowedForEver()
    {
        using var tree = new TempTree();
        Assert.True(ScratchTests.TryLink(tree.At("one"), tree.At("two")), "No link could be made to test with.");
        Assert.True(ScratchTests.TryLink(tree.At("two"), tree.At("one")), "No link could be made to test with.");
        var real = new RealPaths();

        var lost = real.Of(tree.At(Path.Combine("one", "Documents")));

        // Wherever it gave up, it is somewhere in the circle, and says the same when asked again.
        Assert.Contains(lost, new[] { "one", "two" }.Select(name => Path.Combine(real.Of(tree.Root), name, "Documents")));
        Assert.Equal(lost, real.Of(tree.At(Path.Combine("one", "Documents"))));
    }

    [Fact]
    public void AJunctionToAVolumeByItsName_StaysAsItIsSpelled_AndLinksBelowItAreStillFollowed()
    {
        // A folder with a volume mounted in it holds the same kind of target: the volume's name, which is no path.
        // Taken for one, it named a folder that is not there, and no link below it was seen any more.
        if (!OperatingSystem.IsWindows()) return;

        using var tree = new TempTree();
        tree.Write(@"home\Documents\a.txt", "a");
        Directory.CreateDirectory(tree.At(@"data\Dropbox"));
        Assert.True(ScratchTests.TryLink(tree.At(@"data\Dropbox\Documents"), tree.At(@"home\Documents")), "No link could be made to test with.");

        var drive = Path.GetPathRoot(tree.Root)!;
        var volume = new System.Text.StringBuilder(64);
        Assert.True(GetVolumeNameForVolumeMountPoint(drive, volume, volume.Capacity), "The volume's name could not be asked for.");
        Assert.True(ScratchTests.TryLink(tree.At("mounted"), volume + tree.At("data")[drive.Length..]), "No link could be made to test with.");
        Assert.StartsWith("Volume{", new DirectoryInfo(tree.At("mounted")).LinkTarget);
        var real = new RealPaths();

        Assert.Equal(Path.Combine(real.Of(tree.Root), "mounted"), real.Of(tree.At("mounted")));
        Assert.True(real.Same(tree.At(@"mounted\Dropbox\Documents"), tree.At(@"home\Documents")));
        Assert.Equal(real.OfFile(tree.At(@"home\Documents\a.txt"), isLink: false), real.OfFile(tree.At(@"mounted\Dropbox\Documents\a.txt"), isLink: false));
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, System.Text.StringBuilder volumeName, int length);

    [Fact]
    public void ASymbolicLink_MayHoldARelativeTarget_AndMayLeadToAFile()
    {
        // Junctions, which are what a test can make on Windows without being an administrator, lead to folders only and
        // always hold a full path.
        if (OperatingSystem.IsWindows()) return;

        using var tree = new TempTree();
        tree.Write("home/Documents/a.txt", "a");
        Directory.CreateDirectory(tree.At("cloud"));
        Directory.CreateSymbolicLink(tree.At("cloud/Documents"), Path.Combine("..", "home", "Documents"));
        File.CreateSymbolicLink(tree.At("cloud/a.txt"), Path.Combine("Documents", "a.txt"));
        File.CreateSymbolicLink(tree.At("cloud/round.txt"), "round.txt");
        var real = new RealPaths();
        var original = real.OfFile(tree.At("home/Documents/a.txt"), isLink: false);

        Assert.Equal(real.Of(tree.At("home/Documents")), real.Of(tree.At("cloud/Documents")));
        Assert.Equal(original, real.OfFile(tree.At("cloud/Documents/a.txt"), isLink: false));
        Assert.Equal(original, real.OfFile(tree.At("cloud/a.txt"), isLink: true));

        // Not asked where it leads, a link to a file is taken for the file it looks like; one that leads to itself is given up.
        Assert.Equal(Path.Combine(real.Of(tree.At("cloud")), "a.txt"), real.OfFile(tree.At("cloud/a.txt"), isLink: false));
        Assert.Equal(Path.Combine(real.Of(tree.At("cloud")), "round.txt"), real.OfFile(tree.At("cloud/round.txt"), isLink: true));
    }
}
