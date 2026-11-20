using Boatdrill.Core.Adapters;
using Boatdrill.Core.Drill;
using Boatdrill.Core.Sampling;

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
    internal static bool TryLink(string link, string target)
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

    [Fact]
    public void AFolderStillInUse_IsLeftAlone_HoweverOld_AndGoesWhenItsOwnerIsDone()
    {
        // Two opens in one process stand for two Boatdrills: Windows shares a file by handle, and the advisory lock .NET
        // takes on Linux and macOS for an open shared with no one (flock) belongs to the open file, not to the process.
        using var tree = new TempTree();
        var rehearsal = Scratch.Create(tree.Root, "boatdrill-rehearsal-long");
        try
        {
            var batch = Path.Combine(rehearsal.FullName, "restic-x", "C", "a.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(batch)!);
            File.WriteAllText(batch, "restored, not compared yet");
            // Left by a run that ended without deleting its folder: the lock file is there, and no one holds it.
            tree.Write("boatdrill-ended/" + Scratch.LockFile, string.Empty);
            tree.Write("boatdrill-ended/C/b.txt", "restored");

            // A day on, both are old enough to be swept, and one is still being worked in.
            Scratch.Sweep(tree.Root, DateTimeOffset.UtcNow.AddDays(1));

            Assert.Equal("restored, not compared yet", File.ReadAllText(batch));
            Assert.False(Directory.Exists(tree.At("boatdrill-ended")));
        }
        finally
        {
            rehearsal.Dispose();
        }

        Assert.False(Directory.Exists(rehearsal.FullName));
    }

    [Fact]
    public async Task ARehearsalThatOutlastsTheAgeLimit_KeepsItsFolder_ThoughADrillSweepsMeanwhile()
    {
        using var source = new TempTree();
        using var mirror = new TempTree();
        using var scratch = new TempTree();
        for (var i = 0; i < 4; i++) source.Write($"docs/{i}.txt", $"document {i}", DateTimeOffset.UtcNow.AddDays(-3));
        source.MirrorTo(mirror.Root);

        var result = await new RehearsalRunner([new SweptMeanwhile(scratch.Root)]).RunAsync(
            new RehearsalPlan(Source.FromPath(source.Root, "Docs"), Destination.MirrorFolder(mirror.Root, "Drive"), scratch.Root));

        Assert.True(result.AllIntact, result.Sentence);
        TempTree.AssertEmpty(scratch.Root);
    }

    [Fact]
    public void OnWindows_TheRunnersScratchFolder_IsWhereItWas_AndLeftForTheDrillToMake()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var tree = new TempTree();
        Assert.Equal(Path.Combine(Path.GetTempPath(), "boatdrill"), RunnerScratch.Prepare(null));
        Assert.Equal(tree.At("Boatdrill scratch"), RunnerScratch.Prepare(tree.Root));
        Assert.False(Directory.Exists(tree.At("Boatdrill scratch")));
    }

    [Fact]
    public void OnLinuxAndMacOS_TheRunnersScratchFolder_IsInTheUsersCacheFolder()
    {
        if (OperatingSystem.IsWindows()) return;

        Assert.Equal("/home/max/.cache/boatdrill", RunnerScratch.Folder(null, null, "/home/max"));
        Assert.Equal("/srv/cache/max/boatdrill", RunnerScratch.Folder(null, "/srv/cache/max", "/home/max"));
        Assert.Equal("/mnt/big/Boatdrill scratch", RunnerScratch.Folder("/mnt/big", "/srv/cache/max", "/home/max"));

        // The XDG base directory specification has an empty $XDG_CACHE_HOME taken as unset, and a relative one ignored.
        Assert.Equal("/home/max/.cache/boatdrill", RunnerScratch.Folder(null, string.Empty, "/home/max"));
        Assert.Equal("/home/max/.cache/boatdrill", RunnerScratch.Folder(null, "cache", "/home/max"));

        // Without a home folder, ~/.cache would be wherever the runner was started.
        Assert.Contains("--scratch", Assert.Throws<ArgumentException>(() => RunnerScratch.Folder(null, null, string.Empty)).Message);
        Assert.Throws<ArgumentException>(() => RunnerScratch.Folder(null, null, "home/max"));
    }

    [Fact]
    public void OnLinuxAndMacOS_ANewScratchFolder_IsMadeForTheUserAlone()
    {
        if (OperatingSystem.IsWindows()) return;

        using var tree = new TempTree();
        var folder = tree.At("cache/boatdrill");

        Assert.Equal(folder, RunnerScratch.EnsurePrivate(folder));
        Assert.Equal(Scratch.UserOnly, new DirectoryInfo(folder).UnixFileMode);
    }

    [Fact]
    public void OnLinuxAndMacOS_ARunsOwnFolder_IsTheUsersAlone_WhateverTheFolderAboveIt()
    {
        if (OperatingSystem.IsWindows()) return;

        using var tree = new TempTree();
        new DirectoryInfo(tree.Root).UnixFileMode = Scratch.UserOnly | UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        using var run = Scratch.Create(tree.Root, Scratch.Prefix + "run");

        Assert.Equal(Scratch.UserOnly, new DirectoryInfo(run.FullName).UnixFileMode);
    }

    [Fact]
    public void OnLinuxAndMacOS_AScratchFolderOthersCanOpen_IsRefused_AndLeftAsItWas()
    {
        // As the "Boatdrill scratch" folder an earlier version made is.
        if (OperatingSystem.IsWindows()) return;

        using var tree = new TempTree();
        var left = tree.Write("Boatdrill scratch/boatdrill-left/a.txt", "restored earlier");
        var folder = tree.At("Boatdrill scratch");
        var open = Scratch.UserOnly | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        new DirectoryInfo(folder).UnixFileMode = open;

        var refused = Assert.Throws<ArgumentException>(() => RunnerScratch.EnsurePrivate(folder));

        Assert.Equal($"Other users can open the scratch folder {folder}, and so read what Boatdrill restores there. " +
                     "Make it yours alone with chmod 700, delete it, or name another folder with --scratch.", refused.Message);
        Assert.Equal(open, new DirectoryInfo(folder).UnixFileMode);
        Assert.Equal("restored earlier", File.ReadAllText(left));
    }

    [Fact]
    public void OnLinuxAndMacOS_AScratchFolderThatIsALink_IsRefused_AndWhereItLeadsIsLeftAlone()
    {
        if (OperatingSystem.IsWindows()) return;

        using var tree = new TempTree();
        var elsewhere = Directory.CreateDirectory(tree.At("elsewhere"), Scratch.UserOnly).FullName;
        var link = tree.At("Boatdrill scratch");
        Directory.CreateSymbolicLink(link, elsewhere);
        var dangling = tree.At("dangling");
        Directory.CreateSymbolicLink(dangling, tree.At("nowhere"));

        Assert.Contains("is a symbolic link", Assert.Throws<ArgumentException>(() => RunnerScratch.EnsurePrivate(link)).Message);
        Assert.Contains("is a symbolic link", Assert.Throws<ArgumentException>(() => RunnerScratch.EnsurePrivate(dangling)).Message);

        Assert.NotNull(new DirectoryInfo(link).LinkTarget);
        Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere));
        Assert.False(Directory.Exists(tree.At("nowhere")));
    }

    [Fact]
    public void OnLinuxAndMacOS_AScratchFolderOfTheUsersAlone_IsUsedAsItIs()
    {
        if (OperatingSystem.IsWindows()) return;

        using var tree = new TempTree();
        var folder = Directory.CreateDirectory(tree.At("Boatdrill scratch"), Scratch.UserOnly).FullName;
        var left = tree.Write("Boatdrill scratch/boatdrill-left/a.txt", "restored earlier");

        Assert.Equal(folder, RunnerScratch.EnsurePrivate(folder));
        Assert.Equal(Scratch.UserOnly, new DirectoryInfo(folder).UnixFileMode);
        Assert.Equal("restored earlier", File.ReadAllText(left));
    }

    [Fact]
    public void OnLinux_AScratchFolderOfAnotherUser_IsRefused()
    {
        // /root is root's, and on Linux usually root's alone: the nearest thing to another user's folder a test can count
        // on. Root itself may change any folder's mode, so run as root this test has nothing to show.
        const string theirs = "/root";
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess || !Directory.Exists(theirs) || new DirectoryInfo(theirs).LinkTarget is not null) return;

        var refused = Assert.Throws<ArgumentException>(() => RunnerScratch.EnsurePrivate(theirs));

        Assert.Equal($"The scratch folder {theirs} belongs to another user, who could read what Boatdrill restores there. " +
                     "Name a folder of your own with --scratch.", refused.Message);
    }

    /// <summary>
    /// A folder of copies restored a batch at a time into the scratch folder ahead of reading, as restic restores, while
    /// a drill that starts a day later sweeps the scratch directory before each file is read.
    /// </summary>
    private sealed class SweptMeanwhile(string scratchDirectory) : IDestinationAdapter
    {
        private readonly MirrorFolderAdapter _inner = new();
        private string _stage = string.Empty;

        public DestinationKind Kind => DestinationKind.MirrorFolder;

        public Task<DestinationProbe> ProbeAsync(Destination destination, CancellationToken ct) => _inner.ProbeAsync(destination, ct);

        public Task<SourceBinding> BindAsync(Destination destination, Sample sample, string scratch, BindPurpose purpose,
            IReadOnlyCollection<string> alsoRead, CancellationToken ct)
        {
            _stage = Path.Combine(scratch, "stage");
            return _inner.BindAsync(destination, sample, scratch, purpose, alsoRead, ct);
        }

        public Task PrefetchAsync(SourceBinding binding, IReadOnlyCollection<string> relativePaths, CancellationToken ct)
        {
            foreach (var path in relativePaths)
            {
                var staged = Path.Combine(_stage, path);
                Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                File.Copy(Path.Combine(binding.BasePath, path), staged);
            }

            return Task.CompletedTask;
        }

        public Task<CopyLookup> LocateAsync(SourceBinding binding, string relativePath, CancellationToken ct) => _inner.LocateAsync(binding, relativePath, ct);

        public Task<ReadBack> ReadBackAsync(SourceBinding binding, CopyLookup copy, string scratchPath, CancellationToken ct)
        {
            Scratch.Sweep(scratchDirectory, DateTimeOffset.UtcNow.AddDays(1));
            return CopyReader.ReadAsync(Path.Combine(_stage, Path.GetRelativePath(binding.BasePath, copy.Path!)), scratchPath, TimeSpan.Zero, ct);
        }
    }
}
