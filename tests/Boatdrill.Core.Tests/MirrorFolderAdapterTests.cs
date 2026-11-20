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
    public async Task ABackupFolderThatIsALinkToTheProtectedFolder_HoldsNoCopyOfIt()
    {
        // mklink /J Dropbox\Documents Documents, or ln -s ~/Documents ~/Dropbox/Documents, to have a sync app carry the
        // folder: what is in the backup folder is the folder itself, and every file read back from there is the original.
        using var tree = new TempTree();
        using var scratch = new TempTree();
        for (var i = 0; i < 8; i++) tree.Write($"home/Documents/{i}.txt", $"document {i}", DateTimeOffset.UtcNow.AddDays(-3));
        Directory.CreateDirectory(tree.At("Dropbox"));
        Assert.True(ScratchTests.TryLink(tree.At("Dropbox/Documents"), tree.At("home/Documents")), "No link could be made to test with.");
        Assert.True(ScratchTests.TryLink(tree.At("elsewhere"), tree.At("home")), "No link could be made to test with.");
        var source = Source.FromPath(tree.At("home/Documents"), "Documents");

        // The backup folder holds the link, is the link, or reaches the protected folder through a link higher up.
        foreach (var (folder, itself) in new[] { ("Dropbox", "Dropbox/Documents"), ("Dropbox/Documents", "Dropbox/Documents"), ("elsewhere", "elsewhere/Documents") })
        {
            var backup = Destination.MirrorFolder(tree.At(folder), "Dropbox");
            var report = await new Drill.DrillRunner().RunAsync(new Drill.DrillPlan
            {
                Sources = [source],
                Destinations = [backup],
                ScratchDirectory = scratch.Root,
                Sampling = Everything,
            });

            var result = Assert.Single(report.Destinations);
            Assert.True(result.Reachable, result.ProbeDetail);
            Assert.Equal((0, 8), (result.Verified, result.Missing));
            Assert.Equal(Scoring.Verdict.WouldNotRestore, result.Score.Verdict);
            Assert.EndsWith($" Documents: {Path.GetFullPath(tree.At(itself))} is the protected folder itself, not a copy of it.", result.Sentence);

            var scan = await new Scan.OnlyHereScanner().ScanAsync([source], [backup], scratch.Root);
            Assert.Equal((8L, 8L), (scan.OnlyHereFiles, Assert.Single(scan.Sources).NoCopyFiles));

            using (new InKorean())
            {
                var binding = await new MirrorFolderAdapter().BindAsync(backup, new Sampler(Everything).Take(source), scratch.Root, BindPurpose.Read, [], CancellationToken.None);
                Assert.Equal($"{Path.GetFullPath(tree.At(itself))}는 사본이 아니라 보호하는 폴더 자체입니다", binding.Problem);
            }
        }

        // Told to look in the link, it says why it finds nothing there.
        var told = Destination.MirrorFolder(tree.At("Dropbox"), "Dropbox", layout: MirrorLayout.RootFolder);
        var fixedLayout = await new MirrorFolderAdapter().BindAsync(told, new Sampler(Everything).Take(source), scratch.Root, BindPurpose.Read, [], CancellationToken.None);
        Assert.True(fixedLayout.NothingThere);
        Assert.Equal(CopyState.Missing, (await new MirrorFolderAdapter().LocateAsync(fixedLayout, "0.txt", CancellationToken.None)).State);
    }

    [Fact]
    public async Task ALinkInsideTheBackup_ThatLeadsBackToTheOriginals_IsNotReadAsTheirCopy()
    {
        // The backup folder is a real one, with real copies in it, but one folder inside it is a link to the folder it
        // should hold a copy of. Nothing about the layout shows that: only the files found through the link do.
        using var tree = new TempTree();
        using var backup = new TempTree();
        using var scratch = new TempTree();
        var old = DateTimeOffset.UtcNow.AddDays(-3);
        for (var i = 0; i < 4; i++) tree.Write($"letters/{i}.txt", $"letter {i}", old);
        tree.MirrorTo(backup.Root);
        for (var i = 0; i < 3; i++) tree.Write($"photos/{i}.jpg", $"photo {i}", old);
        Assert.True(ScratchTests.TryLink(backup.At("photos"), tree.At("photos")), "No link could be made to test with.");
        var source = Source.FromPath(tree.Root, "Docs");
        var drive = Destination.MirrorFolder(backup.Root, "Drive");

        var report = await new Drill.DrillRunner().RunAsync(new Drill.DrillPlan
        {
            Sources = [source],
            Destinations = [drive],
            ScratchDirectory = scratch.Root,
            Sampling = Everything,
        });

        var result = Assert.Single(report.Destinations);
        Assert.Equal((4, 3), (result.Verified, result.Missing));
        Assert.Equal(
            Enumerable.Range(0, 3).Select(i => $"{backup.At(Path.Combine("photos", $"{i}.jpg"))} is the original itself, reached through a link, not a copy of it"),
            result.Findings.OrderBy(f => f.RelativePath, StringComparer.Ordinal).Select(f => f.Detail));

        var scan = await new Scan.OnlyHereScanner().ScanAsync([source], [drive], scratch.Root);
        Assert.Equal((7L, 3L), (Assert.Single(scan.Sources).Files, scan.Sources[0].NoCopyFiles));

        var rehearsal = await new Drill.RehearsalRunner().RunAsync(new Drill.RehearsalPlan(source, drive, scratch.Root));
        Assert.Equal((7L, 4L, 3L), (rehearsal.Files, rehearsal.Verified, rehearsal.Missing));

        // A symbolic link to one file, where anyone may make one: the "copy" is the original under another name.
        if (!OperatingSystem.IsWindows())
        {
            tree.Write("letters/linked.txt", "a letter", old);
            File.CreateSymbolicLink(backup.At("letters/linked.txt"), tree.At("letters/linked.txt"));
            var binding = await new MirrorFolderAdapter().BindAsync(drive, new Sampler(Everything).Take(source), scratch.Root, BindPurpose.Read, [], CancellationToken.None);

            Assert.Equal(CopyState.Missing, (await new MirrorFolderAdapter().LocateAsync(binding, Path.Combine("letters", "linked.txt"), CancellationToken.None)).State);
            Assert.Equal(CopyState.Found, (await new MirrorFolderAdapter().LocateAsync(binding, Path.Combine("letters", "0.txt"), CancellationToken.None)).State);
        }

        using var korean = new InKorean();
        var copy = await new MirrorFolderAdapter().LocateAsync(
            new SourceBinding(drive, source, MirrorLayout.Contents, backup.Root, 0, 0), Path.Combine("photos", "0.jpg"), CancellationToken.None);
        Assert.Equal((CopyState.Missing, $"{backup.At(Path.Combine("photos", "0.jpg"))}에 있는 파일은 사본이 아니라 링크로 연결된 원본 자체입니다"), (copy.State, copy.Detail));
    }

    [Fact]
    public void TheProtectedFolder_IsItself_HoweverItsPathIsSpelled()
    {
        using var tree = new TempTree();
        var folder = Directory.CreateDirectory(tree.At("Documents")).FullName;
        var source = Source.FromPath(folder);

        Assert.True(MirrorFolderAdapter.IsTheSource(folder + Path.DirectorySeparatorChar, source));
        Assert.True(MirrorFolderAdapter.IsTheSource(Path.Combine(folder, "..", "Documents"), source));
        Assert.False(MirrorFolderAdapter.IsTheSource(tree.At("Documents 2"), source));

        // Windows and macOS take DOCUMENTS for the same folder; on Linux it is another one.
        Assert.Equal(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(), MirrorFolderAdapter.IsTheSource(tree.At("DOCUMENTS"), source));
    }

    [Fact]
    public async Task ABackupAtTheRootOfADrive_IsReadFromThere()
    {
        // boatdrill-cli drill --dest E:\, a mirror whose files sit at the root of its drive, or at / on Linux. This test's
        // "drive" is the one the temp folder is on: only the files it names there are looked at.
        using var tree = new TempTree();
        using var backup = new TempTree();
        using var scratch = new TempTree();
        var root = Path.GetPathRoot(backup.Root)!;
        var below = MirrorFolderAdapter.PathBelowVolume(backup.Root);
        for (var i = 0; i < 3; i++) tree.Write(Path.Combine(below, $"{i}.txt"), $"document {i}", DateTimeOffset.UtcNow.AddDays(-3));
        tree.MirrorTo(root);

        var report = await new Drill.DrillRunner().RunAsync(new Drill.DrillPlan
        {
            Sources = [Source.FromPath(tree.Root, "Docs")],
            Destinations = [Destination.MirrorFolder(root, "Drive")],
            ScratchDirectory = scratch.Root,
            Sampling = Everything,
        });

        var result = Assert.Single(report.Destinations);
        Assert.Equal((3, 0), (result.Verified, result.Missing));
        Assert.Equal((MirrorLayout.Contents, root), (Assert.Single(result.Bindings).Layout, result.Bindings[0].BasePath));

        // A path on another drive or share still leads out of it.
        var binding = new SourceBinding(Destination.MirrorFolder(root), Source.FromPath(tree.Root), MirrorLayout.Contents, root, 0, 0);
        var found = await new MirrorFolderAdapter().LocateAsync(binding, Path.Combine(below, "0.txt"), CancellationToken.None);
        Assert.Equal((CopyState.Found, backup.At("0.txt")), (found.State, found.Path));
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(CopyState.Missing, (await new MirrorFolderAdapter().LocateAsync(binding, @"\\server\share\0.txt", CancellationToken.None)).State);
        }
    }

    [Fact]
    public void WhatIsInsideAFolder_IsTold_ForDrivesSharesTrailingSeparatorsAndCase()
    {
        // The test that keeps a lookup inside the backup, and a rehearsed folder inside its source.
        if (OperatingSystem.IsWindows())
        {
            Assert.True(SourceFiles.IsInside(@"E:\", @"E:\a.txt"));
            Assert.True(SourceFiles.IsInside(@"E:\", @"E:\"));
            Assert.True(SourceFiles.IsInside(@"E:\Backup\", @"E:\Backup\a\b.txt"));
            Assert.True(SourceFiles.IsInside(@"E:\Backup", @"e:\backup\A.TXT"));
            Assert.True(SourceFiles.IsInside(@"E:\Backup", @"E:\Backup\..a.txt"));
            Assert.False(SourceFiles.IsInside(@"E:\Backup", @"E:\Backup2\a.txt"));
            Assert.False(SourceFiles.IsInside(@"E:\Backup", @"E:\a.txt"));
            Assert.False(SourceFiles.IsInside(@"E:\", @"F:\a.txt"));

            Assert.True(SourceFiles.IsInside(@"\\nas\backup", @"\\nas\backup\a.txt"));
            Assert.True(SourceFiles.IsInside(@"\\nas\backup\", @"\\nas\backup\a\b.txt"));
            Assert.False(SourceFiles.IsInside(@"\\nas\backup", @"\\nas\backup2\a.txt"));
            Assert.False(SourceFiles.IsInside(@"\\nas\backup", @"\\nas\other\a.txt"));
            Assert.False(SourceFiles.IsInside(@"\\nas\backup\Laptop", @"\\nas\backup\a.txt"));
        }
        else
        {
            Assert.True(SourceFiles.IsInside("/", "/a.txt"));
            Assert.True(SourceFiles.IsInside("/", "/"));
            Assert.True(SourceFiles.IsInside("/mnt/backup/", "/mnt/backup/a/b.txt"));
            Assert.True(SourceFiles.IsInside("/mnt/backup", "/mnt/backup/..a.txt"));
            Assert.False(SourceFiles.IsInside("/mnt/backup", "/mnt/backup2/a.txt"));
            Assert.False(SourceFiles.IsInside("/mnt/backup", "/mnt/a.txt"));

            // macOS does not tell names apart by case; Linux does.
            Assert.Equal(OperatingSystem.IsMacOS(), SourceFiles.IsInside("/mnt/backup", "/MNT/BACKUP/a.txt"));
        }
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
