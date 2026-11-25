using System.Text.Json;
using Boatdrill.Core.Adapters;
using Boatdrill.Core.Drill;
using Boatdrill.Core.Reporting;
using Boatdrill.Core.Sampling;
using Boatdrill.Core.Scan;
using Boatdrill.Core.Scoring;
using Boatdrill.Core.Settings;
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

    [Fact]
    public async Task APasswordInTheRepositoryUrl_ReachesResticAsTyped_AndNothingElse()
    {
        using var source = new TempTree();
        using var data = new TempTree();
        using var tool = new TempTree();
        using var scratch = new TempTree();
        WriteDocuments(source, 3, LastWeek);
        const string url = "rest:https://max:hunter2@nas/";

        // Saved and read back as the app does, the password encrypted apart from the URL.
        var store = new SettingsStore(data.At("settings.json"), () => new AppSettings(), SettingsStoreTests.Protect);
        store.Update(s => s.Destinations.Add(DestinationSetting.ForRestic(url, "repository password", toolPath: QuotesTheRepository(tool))));
        Assert.DoesNotContain("hunter2", File.ReadAllText(store.FilePath));

        var report = await new DrillRunner().RunAsync(new DrillPlan
        {
            Sources = [Source.FromPath(source.Root, "Docs")],
            Destinations = [.. store.Snapshot().Destinations.Select(d => d.ToDestination(SettingsStoreTests.Unprotect))],
            ScratchDirectory = scratch.Root,
            Sampling = Everything,
        });

        Assert.Equal(url, File.ReadAllText(tool.At("repository.txt")).Trim());
        var result = Assert.Single(report.Destinations);
        Assert.Equal("restic said: Get \"rest:https://max:***@nas/config\": dial tcp: lookup nas: no such host", result.ProbeDetail);
        Assert.Equal("nas", result.DestinationName);
        Assert.DoesNotContain("hunter2", JsonSerializer.Serialize(report, BoatdrillJson.Options));
        Assert.DoesNotContain("hunter2", HtmlReport.Render(report));
    }

    [Fact]
    public async Task ASnapshotThatCannotBeListed_IsNotLookedIn_AndItsFilesAreUnreadable_NotMissing()
    {
        using var source = new TempTree();
        using var tool = new TempTree();
        using var scratch = new TempTree();
        WriteDocuments(source, 3, LastWeek);
        var docs = Source.FromPath(source.Root, "Docs");

        // restic names the snapshot that holds the folder, and then cannot say what is in it.
        const string said = "restic said: unable to load index 4f1c2a9e: connection reset by peer";
        var repository = Destination.Restic(tool.At("repository"), "secret", "Repository", StandIn.Write(tool, "restic", 3,
        [
            new("snapshots", Output: SnapshotOf(source.Root)),
            new("ls", ExitCode: 1, Error: "Fatal: unable to load index 4f1c2a9e: connection reset by peer\n"),
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
    public async Task SnapshotsThatCannotBeListed_AreNotARepositoryThatHoldsNone()
    {
        using var source = new TempTree();
        using var tool = new TempTree();
        using var scratch = new TempTree();
        WriteDocuments(source, 3, LastWeek);

        // Asked again after it answered the drill's first look: a connection that dropped in between, say.
        var repository = Destination.Restic(tool.At("repository"), "secret", "Repository", StandIn.Write(tool, "restic", 3,
            [new("snapshots", ExitCode: 1, Error: "Fatal: reading the snapshots failed: connection reset by peer\n")]));
        var adapter = new ResticAdapter();
        var sample = new Sampler(Everything).Take(Source.FromPath(source.Root, "Docs"));

        var binding = await adapter.BindAsync(repository, sample, scratch.Root, BindPurpose.Read, [], CancellationToken.None);

        Assert.True(binding.CouldNotLook);
        Assert.Equal("restic said: reading the snapshots failed: connection reset by peer", binding.Problem);
        var copy = await adapter.LocateAsync(binding, Path.Combine("docs", "0.txt"), CancellationToken.None);
        Assert.Equal((CopyState.Error, binding.Problem), (copy.State, copy.Detail));
    }

    [Fact]
    public async Task ARepositoryThatAnswers_AndHoldsNoSnapshotOfTheFolder_HasNoCopy()
    {
        using var source = new TempTree();
        using var other = new TempTree();
        using var tool = new TempTree();
        using var scratch = new TempTree();
        WriteDocuments(source, 3, LastWeek);

        // Its one snapshot is of another folder. It is never asked to list it: the stand-in has no answer for that.
        var repository = Destination.Restic(tool.At("repository"), "secret", "Repository", StandIn.Write(tool, "restic", 3,
            [new("snapshots", Output: SnapshotOf(other.Root))]));

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

    [Fact]
    public async Task ARestoreThatStops_LeavesNoFileToTrust_AndIsNotCorruption()
    {
        using var source = new TempTree();
        using var tool = new TempTree();
        WriteDocuments(source, 3, LastWeek);

        // restic writes each file in place, so one it was still writing when it stopped is there under its own name, with
        // some of its bytes. Here it stops without naming any file, as when it is killed for taking too long.
        var repository = Destination.Restic(tool.At("repository"), "secret", "Repository", StandIn.Write(tool, "restic", 3,
        [
            new("snapshots", Output: SnapshotOf(source.Root)),
            new("ls", Output: ListingOf(source.Root, "docs/0.txt", "docs/1.txt", "docs/2.txt")),
            new("version", Output: "restic 0.18.0 compiled with go1.24.4 on linux/amd64\n"),
            new("restore", ExitCode: 1, Error: "Fatal: unable to load pack 7c0a1d33: connection reset by peer\n")
            {
                // The sixth argument is the folder restic restores to: restore <snapshot> --target <folder>.
                Windows = ["mkdir \"%~6\\docs\"", ">\"%~6\\docs\\0.txt\" echo docu"],
                Unix = ["mkdir -p \"$6/docs\"", "printf 'docu' > \"$6/docs/0.txt\""],
            },
        ]));

        var result = await DrillOnce(source.Root, repository);

        Assert.Equal((0, 0, 3), (result.Verified, result.Corrupt, result.Errors));
        Assert.All(result.Findings, f => Assert.Equal((FileOutcome.Error, "restic said: unable to load pack 7c0a1d33: connection reset by peer"), (f.Outcome, f.Detail)));
        Assert.DoesNotContain("corruption", result.Sentence);
    }

    /// <summary>What restic prints for `snapshots --json` when its one snapshot is of this folder, taken on this PC.</summary>
    private static string SnapshotOf(string folder) => JsonSerializer.Serialize(new[]
    {
        new { time = LastWeek.UtcDateTime.ToString("o"), tree = "ab", paths = new[] { folder }, hostname = Environment.MachineName, id = new string('a', 64), short_id = "aaaaaaaa" },
    });

    /// <summary>What restic prints for `ls --json` of a snapshot that holds these files of a folder as they are now: a line each.</summary>
    private static string ListingOf(string folder, params string[] files) => string.Concat(files.Select(file =>
    {
        var info = new FileInfo(Path.Combine(folder, file));
        return JsonSerializer.Serialize(new
        {
            name = info.Name,
            type = "file",
            path = ResticAdapter.TreePath(info.FullName),
            size = info.Length,
            mtime = info.LastWriteTimeUtc.ToString("o"),
            struct_type = "node",
        }) + "\n";
    }));

    /// <summary>
    /// A restic that notes the repository it was given, then fails quoting it in full, as a message about the URL
    /// itself might.
    /// </summary>
    private static string QuotesTheRepository(TempTree folder)
    {
        if (OperatingSystem.IsWindows())
        {
            return folder.Write("restic.cmd", string.Join("\r\n",
                "@echo off",
                ">>\"%~dp0repository.txt\" echo %RESTIC_REPOSITORY%",
                ">&2 echo Fatal: Get \"%RESTIC_REPOSITORY%config\": dial tcp: lookup nas: no such host",
                "exit /b 1",
                string.Empty));
        }

        var script = folder.Write("restic", string.Join("\n",
            "#!/bin/sh",
            "printf '%s\\n' \"$RESTIC_REPOSITORY\" >> \"$(dirname \"$0\")/repository.txt\"",
            "printf 'Fatal: Get \"%sconfig\": dial tcp: lookup nas: no such host\\n' \"$RESTIC_REPOSITORY\" >&2",
            "exit 1",
            string.Empty));
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }
}
