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
