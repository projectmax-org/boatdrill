using Boatdrill.Core.Adapters;
using Boatdrill.Core.Sampling;

namespace Boatdrill.Core.Tests;

public sealed class MirrorFolderAdapterTests
{
    private static readonly SampleOptions Everything = new() { SampleSize = 100, Seed = 1, ChurnWindow = TimeSpan.FromHours(-1) };

    [Theory]
    [InlineData(MirrorLayout.Contents)]
    [InlineData(MirrorLayout.RootFolder)]
    [InlineData(MirrorLayout.FullPath)]
    public async Task Bind_DetectsTheLayoutFromTheSample(MirrorLayout layout)
    {
        using var tree = new TempTree();
        using var backup = new TempTree();
        for (var i = 0; i < 10; i++) tree.Write($"a/{i}.txt", $"{i}");
        var source = Source.FromPath(tree.Root);
        var destination = Destination.MirrorFolder(backup.Root);
        tree.MirrorTo(MirrorFolderAdapter.Candidates(destination, source).First(c => c.Layout == layout).BasePath);

        var sample = new Sampler(Everything).Take(source);
        var binding = await new MirrorFolderAdapter().BindAsync(destination, sample, Path.GetTempPath(), BindPurpose.Read, [], CancellationToken.None);

        Assert.Equal(layout, binding.Layout);
        Assert.Equal(10, binding.ProbeHits);
        Assert.Equal(10, binding.ProbeTried);
    }

    [Fact]
    public void AWholeDrive_IsLookedForInAFolderNamedAfterIt_NeverInItself()
    {
        // "D:\" has no name but itself, and Path.Combine takes a rooted name for the whole path: the source.
        var (drive, backup, folder) = OperatingSystem.IsWindows() ? (@"D:\", @"E:\Backup", @"E:\Backup\D") : ("/", "/mnt/backup", "/mnt/backup/root");
        var source = Source.FromPath(drive);

        var candidates = MirrorFolderAdapter.Candidates(Destination.MirrorFolder(backup), source).ToList();

        Assert.Equal(folder, candidates.Single(c => c.Layout == MirrorLayout.RootFolder).BasePath);
        Assert.DoesNotContain(candidates, c => MirrorFolderAdapter.IsTheSource(c.BasePath, source));

        // An archive of a whole drive holds its files at the top, or below a folder named after the drive letter.
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(["", "D/"], Adapters.ArchiveAdapter.Prefixes(Source.FromPath(@"D:\")).Select(p => p.Prefix).Take(2));
        }
    }

    [Fact]
    public async Task ABackupFolderThatHoldsTheProtectedFolder_IsNeverReadAsItsOwnCopy()
    {
        // Protecting OneDrive\Documents and choosing OneDrive itself as the backup: the "folder named after the source"
        // inside it is the source. Every file would read back from itself and verify.
        using var tree = new TempTree();
        using var scratch = new TempTree();
        for (var i = 0; i < 8; i++) tree.Write($"OneDrive/Documents/{i}.txt", $"document {i}", DateTimeOffset.UtcNow.AddDays(-3));

        var report = await new Drill.DrillRunner().RunAsync(new Drill.DrillPlan
        {
            Sources = [Source.FromPath(tree.At("OneDrive/Documents"), "Documents")],
            Destinations = [Destination.MirrorFolder(tree.At("OneDrive"), "OneDrive")],
            ScratchDirectory = scratch.Root,
            Sampling = Everything,
        });

        var result = Assert.Single(report.Destinations);
        Assert.Equal(0, result.Verified);
        Assert.Equal(8, result.Missing);
        Assert.NotEqual(Scoring.Verdict.WouldRestore, result.Score.Verdict);
        Assert.NotEqual(MirrorLayout.RootFolder, Assert.Single(result.Bindings).Layout);

        // Told to use that layout, it says why it finds nothing rather than reading the originals.
        var fixedLayout = Destination.MirrorFolder(tree.At("OneDrive"), "OneDrive", layout: MirrorLayout.RootFolder);
        var binding = await new MirrorFolderAdapter().BindAsync(fixedLayout, new Sampler(Everything).Take(Source.FromPath(tree.At("OneDrive/Documents"))),
            scratch.Root, BindPurpose.Read, [], CancellationToken.None);
        Assert.True(binding.NothingThere);
        Assert.Contains("the protected folder itself", binding.Problem);
        Assert.Equal(Adapters.CopyState.Missing, (await new MirrorFolderAdapter().LocateAsync(binding, "0.txt", CancellationToken.None)).State);
    }

    [Fact]
    public async Task APathThatLeadsOutOfTheBackup_IsNotLookedUp()
    {
        using var backup = new TempTree();
        using var elsewhere = new TempTree();
        var outside = elsewhere.Write("secret.txt", "not part of the backup");
        var binding = new SourceBinding(Destination.MirrorFolder(backup.Root), Source.FromPath(elsewhere.Root), MirrorLayout.Contents, backup.Root, 0, 0);

        Assert.Equal(Adapters.CopyState.Missing, (await new MirrorFolderAdapter().LocateAsync(binding, outside, CancellationToken.None)).State);
        Assert.Equal(Adapters.CopyState.Missing, (await new MirrorFolderAdapter().LocateAsync(binding, Path.Combine("..", Path.GetFileName(elsewhere.Root), "secret.txt"), CancellationToken.None)).State);
    }

    [Fact]
    public async Task Bind_HonoursAFixedLayout()
    {
        using var tree = new TempTree();
        using var backup = new TempTree();
        tree.Write("x.txt", "x");
        var source = Source.FromPath(tree.Root);
        var destination = Destination.MirrorFolder(backup.Root, layout: MirrorLayout.RootFolder);

        var binding = await new MirrorFolderAdapter().BindAsync(destination, new Sampler(Everything).Take(source), Path.GetTempPath(), BindPurpose.Read, [], CancellationToken.None);

        Assert.Equal(MirrorLayout.RootFolder, binding.Layout);
        Assert.Equal(Path.Combine(backup.Root, Path.GetFileName(tree.Root)), binding.BasePath);
    }

    [Fact]
    public async Task Locate_ReportsAMissingCopy()
    {
        using var tree = new TempTree();
        using var backup = new TempTree();
        tree.Write("x.txt", "x");
        var source = Source.FromPath(tree.Root);
        var destination = Destination.MirrorFolder(backup.Root, layout: MirrorLayout.Contents);
        var adapter = new MirrorFolderAdapter();
        var sample = new Sampler(Everything).Take(source);
        var binding = await adapter.BindAsync(destination, sample, Path.GetTempPath(), BindPurpose.Read, [], CancellationToken.None);

        var copy = await adapter.LocateAsync(binding, sample.Files[0].RelativePath, CancellationToken.None);

        Assert.Equal(CopyState.Missing, copy.State);
        Assert.Equal(Path.Combine(backup.Root, "x.txt"), copy.Path);
    }

    [Fact]
    public async Task ReadBack_RestoresTheCopyAndHashesIt()
    {
        using var backup = new TempTree();
        using var scratch = new TempTree();
        var copyPath = backup.Write("hello.txt", "hello");
        var copy = new CopyLookup(CopyState.Found, copyPath, 5, DateTimeOffset.UtcNow, null);
        var scratchFile = scratch.At("restored");

        var source = Source.FromPath(backup.Root);
        var binding = new SourceBinding(Destination.MirrorFolder(backup.Root), source, MirrorLayout.Contents, backup.Root, 0, 0);
        var readBack = await new MirrorFolderAdapter().ReadBackAsync(binding, copy, scratchFile, CancellationToken.None);

        Assert.True(readBack.Success);
        Assert.Equal(5, readBack.BytesRead);
        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", readBack.Sha256);
        Assert.Equal("hello", File.ReadAllText(scratchFile));
    }

    [Fact]
    public async Task Probe_MissingRootIsUnreachable()
    {
        var destination = Destination.MirrorFolder(Path.Combine(Path.GetTempPath(), "boatdrill-not-here"));

        var probe = await new MirrorFolderAdapter().ProbeAsync(destination, CancellationToken.None);

        Assert.False(probe.Reachable);
        Assert.Contains("was not found", probe.Detail);
    }

    [Fact]
    public void VolumeFolder_NamesTheVolumeWithoutPunctuation()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("C", MirrorFolderAdapter.VolumeFolder(@"C:\Users\max\Documents"));
            Assert.Equal(@"Users\max\Documents", MirrorFolderAdapter.PathBelowVolume(@"C:\Users\max\Documents"));
        }
        else
        {
            Assert.Equal("root", MirrorFolderAdapter.VolumeFolder("/home/max/Documents"));
            Assert.Equal("home/max/Documents", MirrorFolderAdapter.PathBelowVolume("/home/max/Documents"));
        }
    }
}
