using System.Text.Json;
using Boatdrill.Core.Adapters;
using Boatdrill.Core.Drill;
using Boatdrill.Core.Sampling;
using Boatdrill.Core.Scan;
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
        TempTree.AssertEmpty(scratch.Root);
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

    [Fact]
    public async Task ASnapshotThatCannotBeListed_IsNotLookedIn_AndItsFilesAreUnreadable_NotMissing()
    {
        using var source = new TempTree();
        using var folder = new TempTree();
        using var scratch = new TempTree();
        WriteDocuments(source, 3, LastWeek);
        var docs = Source.FromPath(source.Root, "Docs");

        // kopia connects and names the snapshot that holds the folder, and then cannot say what is in it.
        const string said = "kopia said: unable to list directory: connection reset by peer";
        var repository = Repository(folder, StandIn.Write(folder, "kopia", 1,
        [
            new("repository"),
            new("snapshot", Output: Snapshots((source.Root, FolderId('a')))),
            new("ls", ExitCode: 1, Error: "ERROR unable to list directory: connection reset by peer\n"),
        ]));

        var result = await DrillOnce(source.Root, repository);
        Assert.True(result.Reachable, result.ProbeDetail);
        Assert.Equal((0, 0, 3), (result.Verified, result.Missing, result.Errors));
        Assert.All(result.Findings, f => Assert.Equal((FileOutcome.Error, said), (f.Outcome, f.Detail)));
        Assert.Equal($"Repository would not restore. 0 of 3 sampled files read back correctly and 3 could not be read. Docs: {said}.", result.Sentence);

        var scan = await new OnlyHereScanner().ScanAsync([docs], [repository], scratch.Root);
        Assert.Empty(scan.Checked);
        Assert.Equal($"Repository: {said}", Assert.Single(scan.Unreachable));
        Assert.Equal(0, scan.OnlyHereFiles);
        Assert.Equal("None of the backups could be looked in, so nothing was compared.", scan.Sentence);

        var rehearsal = await new RehearsalRunner().RunAsync(new RehearsalPlan(docs, repository, scratch.Root));
        Assert.Equal((0L, 0L), (rehearsal.Missing, rehearsal.Errors));
        Assert.Equal($"Nothing was restored from Repository: {said}.", rehearsal.Sentence);
        TempTree.AssertEmpty(scratch.Root);
    }

    [Fact]
    public async Task OneFolderThatCannotBeListed_FailsTheOthersListedBeforeIt()
    {
        using var source = new TempTree();
        using var folder = new TempTree();
        WriteDocuments(source, 2, LastWeek);
        source.Write("photos/beach.raw", "sand", LastWeek);
        var listed = new DateTime(2026, 11, 2, 9, 30, 0, DateTimeKind.Local);

        // Two folders inside the source have snapshots of their own; kopia lists the first and fails at the second. The
        // stand-in tells kopia's commands apart by their fourth word here, which for ls is the folder to list.
        var repository = Repository(folder, StandIn.Write(folder, "kopia", 4,
        [
            new("--path"),
            new("--all", Output: Snapshots((source.At("docs"), FolderId('a')), (source.At("photos"), FolderId('b')))),
            new(FolderId('a'), Output: KopiaAdapterTests.Line("-rw-r--r--", 10, listed, new string('1', 32), "0.txt") + "\n" +
                                     KopiaAdapterTests.Line("-rw-r--r--", 10, listed, new string('2', 32), "1.txt") + "\n"),
            new(FolderId('b'), ExitCode: 1, Error: "ERROR unable to list directory: connection reset by peer\n"),
        ]));

        var result = await DrillOnce(source.Root, repository);

        // Not one file is taken for found by what was listed, nor for missing by what was not.
        Assert.Equal((0, 0, 3), (result.Verified, result.Missing, result.Errors));
        Assert.All(result.Findings, f => Assert.Equal("kopia said: unable to list directory: connection reset by peer", f.Detail));
    }

    [Fact]
    public async Task AKopiaThatFailsOnTheWay_HasNotSaidTheRepositoryHoldsNothing()
    {
        using var source = new TempTree();
        using var folder = new TempTree();
        using var scratch = new TempTree();
        WriteDocuments(source, 3, LastWeek);
        var sample = new Sampler(Everything).Take(Source.FromPath(source.Root, "Docs"));
        var adapter = new KopiaAdapter();

        // Bound without the drill's first look, as when the repository stops answering after it.
        async Task<(SourceBinding Binding, CopyLookup Copy)> BindAsync(string name, StandIn.Answer[] answers)
        {
            var binding = await adapter.BindAsync(Repository(folder, StandIn.Write(folder, name, 1, answers)), sample, scratch.At(name), BindPurpose.ListOnly, [],
                CancellationToken.None);
            return (binding, await adapter.LocateAsync(binding, Path.Combine("docs", "0.txt"), CancellationToken.None));
        }

        // The snapshot is of the folder above the source, so kopia is asked to show that folder on the way down to it.
        var above = Snapshots((Path.GetDirectoryName(source.Root)!, FolderId('a')));
        const string error = "ERROR unable to open repository: connection reset by peer\n";
        const string said = "kopia said: unable to open repository: connection reset by peer";

        foreach (var (name, answers) in new (string, StandIn.Answer[])[]
                 {
                     ("refused", [new("repository", ExitCode: 1, Error: error)]),
                     ("unlisted", [new("repository"), new("snapshot", ExitCode: 1, Error: error)]),
                     ("unshown", [new("repository"), new("snapshot", Output: above), new("show", ExitCode: 1, Error: error)]),
                 })
        {
            var (binding, copy) = await BindAsync(name, answers);
            Assert.True(binding.CouldNotLook, name);
            Assert.Equal((CopyState.Error, said, said), (copy.State, copy.Detail, binding.Problem));
        }

        // Shown, and the source's folder is not in it: kopia has answered, and there is no copy.
        var (absent, none) = await BindAsync("absent",
        [
            new("repository"),
            new("snapshot", Output: above),
            new("show", Output: """{"stream":"kopia:directory","entries":[{"name":"elsewhere","type":"d","obj":"k0a1b2c"}]}"""),
        ]);
        Assert.False(absent.CouldNotLook);
        Assert.Equal(CopyState.Missing, none.State);
        Assert.EndsWith($" does not hold {source.Root}", none.Detail);
    }

    [Fact]
    public async Task ARepositoryThatAnswers_AndHoldsNoSnapshotOfTheFolder_HasNoCopy()
    {
        using var source = new TempTree();
        using var other = new TempTree();
        using var folder = new TempTree();
        using var scratch = new TempTree();
        WriteDocuments(source, 3, LastWeek);

        // Its one snapshot is of another folder. It is never asked to list it: the stand-in has no answer for that.
        var repository = Repository(folder, StandIn.Write(folder, "kopia", 1, [new("repository"), new("snapshot", Output: Snapshots((other.Root, FolderId('a'))))]));

        var result = await DrillOnce(source.Root, repository);
        Assert.Equal((0, 3, 0), (result.Verified, result.Missing, result.Errors));
        Assert.Equal(
            $"Repository would not restore. 0 of 3 sampled files read back correctly and 3 have no copy. Docs: no snapshot in the repository holds {source.Root}.",
            result.Sentence);

        var scan = await new OnlyHereScanner().ScanAsync([Source.FromPath(source.Root, "Docs")], [repository], scratch.Root);
        Assert.Equal(["Repository"], scan.Checked);
        Assert.Empty(scan.Unreachable);
        Assert.Equal(3, Assert.Single(scan.Sources).NoCopyFiles);
    }

    /// <summary>A folder that passes for a Kopia repository, read with this stand-in for kopia.</summary>
    private static Destination Repository(TempTree folder, string kopia)
    {
        folder.Write("kopia.repository.f", "{}");
        return Destination.Kopia(folder.Root, "secret", "Repository", kopia);
    }

    /// <summary>A folder's object ID as kopia writes one: a k and 32 hexadecimal digits.</summary>
    private static string FolderId(char digit) => "k" + new string(digit, 32);

    /// <summary>What kopia prints for `snapshot list --json` when each of these folders has one snapshot, taken on this PC.</summary>
    private static string Snapshots(params (string Folder, string ObjectId)[] folders) => JsonSerializer.Serialize(folders.Select((f, i) => new
    {
        id = new string((char)('a' + i), 32),
        source = new { host = Environment.MachineName.ToLowerInvariant(), userName = "max", path = f.Folder },
        startTime = LastWeek.UtcDateTime.ToString("o"),
        rootEntry = new { name = Path.GetFileName(f.Folder), type = "d", obj = f.ObjectId },
    }));
}
