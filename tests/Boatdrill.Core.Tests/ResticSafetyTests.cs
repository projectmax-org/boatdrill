using System.Text.Json;
using Boatdrill.Core.Drill;
using Boatdrill.Core.Reporting;
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
