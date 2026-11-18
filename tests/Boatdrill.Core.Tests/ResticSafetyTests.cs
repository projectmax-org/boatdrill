using Boatdrill.Core.Drill;
using Boatdrill.Core.Scoring;
using Boatdrill.Core.Verification;
using static Boatdrill.Core.Tests.ResticAdapterTests;

namespace Boatdrill.Core.Tests;

/// <summary>What a restic drill must never do, and how it fails when it cannot read the repository.</summary>
public sealed class ResticSafetyTests
{
    [ResticFact]
    public async Task DamageInTheRepository_IsCorruption()
    {
        using var source = new TempTree();
        using var repository = new ResticRepository();
        WriteDocuments(source, 5, LastWeek);
        source.Write("photos/beach.raw", RandomBytes(400_000), LastWeek);
        repository.Backup(source.Root);
        repository.DamageLargestPack();

        var result = await DrillOnce(source.Root, repository.Destination());

        Assert.Equal(5, result.Verified);
        Assert.Equal(1, result.Corrupt);
        var damaged = Assert.Single(result.Findings);
        Assert.Equal(FileOutcome.Corrupt, damaged.Outcome);
        Assert.StartsWith("the copy in the repository is damaged; restic said: ", damaged.Detail);
        Assert.True(result.Score.Total <= Scorer.CorruptionCap);
        Assert.Contains("points to corruption", result.Sentence);
    }

    [ResticFact]
    public async Task ADrill_LeavesTheRepositoryAsItWas_AndEmptiesTheScratchFolder()
    {
        using var source = new TempTree();
        using var repository = new ResticRepository();
        using var scratch = new TempTree();
        WriteDocuments(source, 10, LastWeek);
        source.Write("docs/report [final].txt", "fetched with dump", LastWeek);
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
        TempTree.AssertEmpty(scratch.Root);
    }

    [ResticFact]
    public async Task AWrongPassword_IsUnreachable_AndSaysSo()
    {
        using var source = new TempTree();
        using var repository = new ResticRepository();
        WriteDocuments(source, 3, LastWeek);
        repository.Backup(source.Root);

        var result = await DrillOnce(source.Root, repository.Destination("not the password"));

        Assert.False(result.Reachable);
        Assert.Equal("the password was not accepted", result.ProbeDetail);
        Assert.Equal("Repository could not be reached: the password was not accepted. Check the repository in Settings, then run a drill.", result.Sentence);
    }

    [ResticFact]
    public async Task AFolderWithoutARepository_IsUnreachable()
    {
        using var source = new TempTree();
        using var empty = new TempTree();
        WriteDocuments(source, 3, LastWeek);

        var result = await DrillOnce(source.Root, Destination.Restic(empty.Root, ResticRepository.Password, "Repository", ResticRepository.Tool));

        Assert.False(result.Reachable);
        Assert.Equal("no restic repository was found at " + empty.Root, result.ProbeDetail);
    }

    [Fact]
    public async Task WithoutRestic_TheRepositoryIsUnreachable_AndSaysWhy()
    {
        using var source = new TempTree();
        using var folder = new TempTree();
        WriteDocuments(source, 3, LastWeek);

        var result = await DrillOnce(source.Root, Destination.Restic(folder.Root, "secret", "Repository", toolPath: folder.At("no-such-restic")));

        Assert.False(result.Reachable);
        Assert.Equal("restic could not be started; install it, or say where it is in Settings", result.ProbeDetail);
    }

    [Fact]
    public async Task WithoutAPassword_TheRepositoryIsUnreachable()
    {
        using var source = new TempTree();
        using var folder = new TempTree();
        WriteDocuments(source, 3, LastWeek);

        var result = await DrillOnce(source.Root, Destination.Restic(folder.Root, null, "Repository"));

        Assert.False(result.Reachable);
        Assert.Equal("no password is saved for it", result.ProbeDetail);
    }
}
