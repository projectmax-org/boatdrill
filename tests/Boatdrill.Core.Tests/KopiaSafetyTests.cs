using Boatdrill.Core.Drill;
using Boatdrill.Core.Scoring;
using Boatdrill.Core.Verification;
using static Boatdrill.Core.Tests.ResticAdapterTests;

namespace Boatdrill.Core.Tests;

/// <summary>What a Kopia drill must never do, and how it fails when it cannot read the repository.</summary>
public sealed class KopiaSafetyTests
{
    [KopiaFact]
    public async Task DamageInTheRepository_IsCorruption()
    {
        using var source = new TempTree();
        using var repository = new KopiaRepository();
        WriteDocuments(source, 5, LastWeek);
        source.Write("photos/beach.raw", RandomBytes(400_000), LastWeek);
        repository.Backup(source.Root);
        repository.DamageLargestPack();

        var result = await DrillOnce(source.Root, repository.Destination());

        Assert.Equal(5, result.Verified);
        Assert.Equal(1, result.Corrupt);
        var damaged = Assert.Single(result.Findings);
        Assert.Equal(FileOutcome.Corrupt, damaged.Outcome);
        Assert.StartsWith("the copy in the repository is damaged; kopia said: ", damaged.Detail);
        Assert.True(result.Score.Total <= Scorer.CorruptionCap);
        Assert.Contains("points to corruption", result.Sentence);
    }

    [KopiaFact]
    public async Task ADrill_LeavesTheRepositoryAsItWas_AndEmptiesTheScratchFolder()
    {
        using var source = new TempTree();
        using var repository = new KopiaRepository();
        using var scratch = new TempTree();
        WriteDocuments(source, 10, LastWeek);
        source.Write("photos/beach.raw", RandomBytes(200_000), LastWeek);
        repository.Backup(source.Root);
        var before = repository.Fingerprint();

        var report = await new DrillRunner().RunAsync(new DrillPlan
        {
            Sources = [Source.FromPath(source.Root, "Docs")],
            Destinations = [repository.Destination()],
            ScratchDirectory = scratch.Root,
            Sampling = Everything,
        });

        Assert.Equal(11, Assert.Single(report.Destinations).Verified);
        Assert.Equal(before, repository.Fingerprint());
        Assert.Empty(Directory.EnumerateFileSystemEntries(scratch.Root));
    }

    [KopiaFact]
    public async Task AWrongPassword_IsUnreachable_AndSaysSo()
    {
        using var source = new TempTree();
        using var repository = new KopiaRepository();
        WriteDocuments(source, 3, LastWeek);
        repository.Backup(source.Root);

        var result = await DrillOnce(source.Root, repository.Destination("not the password"));

        Assert.False(result.Reachable);
        Assert.Equal("the password was not accepted", result.ProbeDetail);
        Assert.Equal("Repository could not be reached: the password was not accepted. Check the repository in Settings, then run a drill.", result.Sentence);
    }

    [Fact]
    public async Task WithoutKopia_TheRepositoryIsUnreachable_AndSaysWhy()
    {
        using var source = new TempTree();
        using var folder = new TempTree();
        WriteDocuments(source, 3, LastWeek);
        folder.Write("kopia.repository.f", "{}");

        var result = await DrillOnce(source.Root, Destination.Kopia(folder.Root, "secret", "Repository", toolPath: folder.At("no-such-kopia")));

        Assert.False(result.Reachable);
        Assert.Equal("kopia could not be started; install it, or say where it is in Settings", result.ProbeDetail);
    }

    [Fact]
    public async Task WithoutAPassword_TheRepositoryIsUnreachable()
    {
        using var source = new TempTree();
        using var folder = new TempTree();
        WriteDocuments(source, 3, LastWeek);

        var result = await DrillOnce(source.Root, Destination.Kopia(folder.Root, null, "Repository"));

        Assert.False(result.Reachable);
        Assert.Equal("no password is saved for it", result.ProbeDetail);
    }

    [Fact]
    public async Task AnUnpluggedDrive_IsUnreachable_BeforeKopiaIsAsked()
    {
        using var source = new TempTree();
        using var folder = new TempTree();
        WriteDocuments(source, 3, LastWeek);
        var gone = folder.At("unplugged");

        var result = await DrillOnce(source.Root, Destination.Kopia(gone, "secret", "Repository", toolPath: folder.At("no-such-kopia")));

        Assert.False(result.Reachable);
        Assert.Equal($"{gone} was not found", result.ProbeDetail);
    }
}
