using System.Text.Json;
using Boatdrill.Core.Drill;
using Boatdrill.Core.History;
using Boatdrill.Core.Notifications;
using Boatdrill.Core.Reporting;
using Boatdrill.Core.Sampling;
using Boatdrill.Core.Scoring;
using Boatdrill.Core.Verification;

namespace Boatdrill.Core.Tests;

public sealed class DrillRunnerTests
{
    private static readonly SampleOptions Everything = new()
    {
        SampleSize = 1000,
        Seed = 1,
        ChurnWindow = TimeSpan.FromHours(-1),
        PerFolderFloor = 0,
    };

    private static DrillPlan Plan(TempTree scratch, Source source, params Destination[] destinations) => new()
    {
        Sources = [source],
        Destinations = destinations,
        ScratchDirectory = scratch.Root,
        Sampling = Everything,
    };

    private static void WriteDocuments(TempTree tree, int count, DateTimeOffset when)
    {
        for (var i = 0; i < count; i++) tree.Write($"docs/{i}.txt", $"document {i}", when);
    }

    [Fact]
    public async Task CompleteMirror_WouldRestore()
    {
        using var tree = new TempTree();
        using var backup = new TempTree();
        using var scratch = new TempTree();
        WriteDocuments(tree, 25, DateTimeOffset.UtcNow.AddDays(-2));
        tree.MirrorTo(backup.Root);

        var report = await new DrillRunner().RunAsync(Plan(scratch, Source.FromPath(tree.Root, "Docs"), Destination.MirrorFolder(backup.Root, "Drive")));

        var result = Assert.Single(report.Destinations);
        Assert.True(result.Reachable);
        Assert.Equal(25, result.Sampled);
        Assert.Equal(25, result.Verified);
        Assert.Empty(result.Findings);
        Assert.Equal(100, result.Score.Total);
        Assert.Equal(Verdict.WouldRestore, result.Score.Verdict);
        Assert.StartsWith("Drive would restore.", result.Sentence);
        Assert.Contains("25 of 25 sampled files read back correctly", result.Sentence);
        Assert.Null(result.BehindSince);
        Assert.NotNull(result.EstimatedFullRestore);
        Assert.Equal(Verdict.WouldRestore, report.Worst);
        var summary = Assert.Single(report.Sources);
        Assert.Equal(25, summary.Population);
    }

    [Fact]
    public async Task MissingCopies_LowerCoverageAndSayHowMany()
    {
        using var tree = new TempTree();
        using var backup = new TempTree();
        using var scratch = new TempTree();
        WriteDocuments(tree, 20, DateTimeOffset.UtcNow.AddDays(-2));
        tree.MirrorTo(backup.Root, skip: ["docs/0.txt", "docs/1.txt", "docs/2.txt", "docs/3.txt", "docs/4.txt"]);

        var report = await new DrillRunner().RunAsync(Plan(scratch, Source.FromPath(tree.Root, "Docs"), Destination.MirrorFolder(backup.Root, "Drive")));

        var result = Assert.Single(report.Destinations);
        Assert.Equal(5, result.Missing);
        Assert.Equal(15, result.Verified);
        Assert.Equal(0.75, result.Score.Coverage, 3);
        Assert.Equal(0, result.Score.CoverageMargin); // every file was sampled, so there is no sampling error
        Assert.Equal(Verdict.WouldMostlyRestore, result.Score.Verdict);
        Assert.Contains("5 have no copy", result.Sentence);
        Assert.Equal(5, result.Findings.Count(f => f.Outcome == FileOutcome.Missing));
    }

    [Fact]
    public async Task CorruptCopy_IsFlaggedAndCapsTheScore()
    {
        using var tree = new TempTree();
        using var backup = new TempTree();
        using var scratch = new TempTree();
        var when = DateTimeOffset.UtcNow.AddDays(-2);
        WriteDocuments(tree, 20, when);
        tree.MirrorTo(backup.Root);
        var damaged = backup.At(Path.Combine("docs", "3.txt"));
        File.WriteAllText(damaged, "document X"); // same length, different bytes
        File.SetLastWriteTimeUtc(damaged, when.UtcDateTime);

        var report = await new DrillRunner().RunAsync(Plan(scratch, Source.FromPath(tree.Root, "Docs"), Destination.MirrorFolder(backup.Root, "Drive")));

        var result = Assert.Single(report.Destinations);
        Assert.Equal(1, result.Corrupt);
        Assert.Equal(19, result.Verified);
        Assert.True(result.Score.Total <= Scorer.CorruptionCap);
        Assert.True(result.Score.Verdict >= Verdict.NeedsAttention);
        Assert.Contains("points to corruption", result.Sentence);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(FileOutcome.Corrupt, finding.Outcome);
        Assert.Contains("same size and date", finding.Detail);
    }

    [Fact]
    public async Task OutdatedCopy_SetsBehindSince()
    {
        using var tree = new TempTree();
        using var backup = new TempTree();
        using var scratch = new TempTree();
        WriteDocuments(tree, 20, DateTimeOffset.UtcNow.AddDays(-5));
        tree.MirrorTo(backup.Root);
        var edited = DateTimeOffset.UtcNow.AddHours(-1);
        tree.Write("docs/7.txt", "document 7, second draft", edited);

        var report = await new DrillRunner().RunAsync(Plan(scratch, Source.FromPath(tree.Root, "Docs"), Destination.MirrorFolder(backup.Root, "Drive")));

        var result = Assert.Single(report.Destinations);
        Assert.Equal(1, result.Outdated);
        Assert.NotNull(result.BehindSince);
        Assert.Equal(edited.UtcDateTime, result.BehindSince!.Value.UtcDateTime, TimeSpan.FromSeconds(1));
        Assert.NotNull(result.Lag);
        Assert.True(result.Lag > TimeSpan.FromMinutes(50));
        Assert.True(result.Score.Freshness < 1);
        Assert.Contains("1 is older than the original", result.Sentence);
        Assert.Contains("The oldest change not yet copied here is from", result.Sentence);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(FileOutcome.Outdated, finding.Outcome);
    }

    [Fact]
    public async Task AProtectedFolderThatIsGone_IsNamed_ThoughTheBackupWouldRestoreTheRest()
    {
        using var tree = new TempTree();
        using var elsewhere = new TempTree();
        using var backup = new TempTree();
        using var scratch = new TempTree();
        WriteDocuments(tree, 10, DateTimeOffset.UtcNow.AddDays(-2));
        tree.MirrorTo(backup.Root);
        var gone = Source.FromPath(elsewhere.At("Pictures"));

        var report = await new DrillRunner().RunAsync(new DrillPlan
        {
            Sources = [Source.FromPath(tree.Root, "Docs"), gone],
            Destinations = [Destination.MirrorFolder(backup.Root, "Drive")],
            ScratchDirectory = scratch.Root,
            Sampling = Everything,
        });

        // No backup is at fault for a folder that is not there, so the verdict stands; the drill still has to say it.
        var result = Assert.Single(report.Destinations);
        Assert.Equal(Verdict.WouldRestore, result.Score.Verdict);
        Assert.Equal(10, result.Verified);
        Assert.Equal([false, true], report.Sources.Select(s => s.Missing));
        Assert.Equal("Pictures", Assert.Single(report.MissingSources).Name);
        Assert.Equal($"Pictures was not found at {gone.RootPath}, so none of it was checked.", Verdicts.MissingSources(report.MissingSources));
        Assert.Equal("Pictures was not found", Verdicts.MissingHeadline(report.MissingSources));

        var notice = NotificationPolicy.For(report);
        Assert.NotNull(notice);
        Assert.Equal("Pictures was not found", notice.Title);
        Assert.EndsWith("so none of it was checked. Check the folder in Settings.", notice.Text);

        var html = HtmlReport.Render(report);
        Assert.Contains("<h1><span class=\"dot bad-fill\" aria-hidden=\"true\"></span>Pictures was not found</h1>", html);
        Assert.Contains("the whole folder: it was not found", html);

        // In the history the flag travels with the source; the list made from it is not written twice.
        var line = JsonSerializer.Serialize(report, BoatdrillJson.Options);
        Assert.Contains("\"missing\":true", line);
        Assert.DoesNotContain("missingSources", line);
        Assert.Single(JsonSerializer.Deserialize<DrillReport>(line, BoatdrillJson.Options)!.MissingSources);
    }

    [Fact]
    public async Task AFolderWithNoCopyAtAll_KeepsTheBackupFromReadingAsARestore()
    {
        // A drive holding Documents but not the Desktop: 20 of 22 files verify, and the whole Desktop would be lost.
        using var tree = new TempTree();
        using var backup = new TempTree();
        using var scratch = new TempTree();
        for (var i = 0; i < 20; i++) tree.Write($"Documents/{i}.txt", $"document {i}", DateTimeOffset.UtcNow.AddDays(-3));
        for (var i = 0; i < 2; i++) tree.Write($"Desktop/{i}.txt", $"note {i}", DateTimeOffset.UtcNow.AddDays(-3));
        tree.MirrorTo(backup.Root, skip: ["Desktop/0.txt", "Desktop/1.txt"]);

        var report = await new DrillRunner().RunAsync(new DrillPlan
        {
            Sources = [Source.FromPath(tree.At("Documents")), Source.FromPath(tree.At("Desktop"))],
            Destinations = [Destination.MirrorFolder(backup.Root, "Drive")],
            ScratchDirectory = scratch.Root,
            Sampling = Everything,
        });

        var result = Assert.Single(report.Destinations);
        Assert.Equal((20, 2), (result.Verified, result.Missing));
        Assert.True(result.Score.Total <= Scorer.CorruptionCap, $"scored {result.Score.Total}");
        Assert.Equal(Verdict.NeedsAttention, result.Score.Verdict);
        Assert.Contains("No copy of Desktop was found", result.Sentence);
    }

    [Fact]
    public async Task OneFoldersFreshCopies_DoNotHideAnotherFoldersStalledBackup()
    {
        using var tree = new TempTree();
        using var backup = new TempTree();
        using var scratch = new TempTree();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 10; i++) tree.Write($"Documents/{i}.txt", $"document {i}", now.AddDays(-3));
        for (var i = 0; i < 10; i++) tree.Write($"Pictures/{i}.jpg", $"photo {i}", now.AddDays(-60));
        tree.MirrorTo(backup.Root);

        // The Pictures job stopped: two photos edited 40 days ago never reached the backup.
        var stalled = now.AddDays(-40);
        tree.Write("Pictures/0.jpg", "photo 0, edited", stalled);
        tree.Write("Pictures/1.jpg", "photo 1, edited", stalled.AddDays(1));

        var report = await new DrillRunner().RunAsync(new DrillPlan
        {
            Sources = [Source.FromPath(tree.At("Documents")), Source.FromPath(tree.At("Pictures"))],
            Destinations = [Destination.MirrorFolder(backup.Root, "NAS")],
            ScratchDirectory = scratch.Root,
            Sampling = Everything,
        });

        var result = Assert.Single(report.Destinations);
        Assert.Equal(2, result.Outdated);
        Assert.Equal(stalled.UtcDateTime, result.BehindSince!.Value.UtcDateTime, TimeSpan.FromSeconds(1));
        Assert.Equal(0, result.Score.Freshness);
        Assert.NotEqual(Verdict.WouldRestore, result.Score.Verdict);
    }

    [Fact]
    public async Task AFileSavedDuringTheDrill_IsBehind_NotCorrupt()
    {
        using var tree = new TempTree();
        using var backup = new TempTree();
        using var scratch = new TempTree();
        for (var i = 0; i < 5; i++) tree.Write($"docs/{i}.txt", $"document {i}", DateTimeOffset.UtcNow.AddDays(-1));
        tree.MirrorTo(backup.Root);
        var budget = tree.At(Path.Combine("docs", "3.txt"));

        var report = await new DrillRunner([new SavesWhileItIsRead(budget)]).RunAsync(Plan(scratch, Source.FromPath(tree.Root, "Docs"), Destination.MirrorFolder(backup.Root, "Drive")));

        var result = Assert.Single(report.Destinations);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(FileOutcome.Outdated, finding.Outcome);
        Assert.Equal((0, 4), (result.Corrupt, result.Verified));
        Assert.Equal(new FileInfo(budget).Length, finding.SourceLength);
    }

    /// <summary>A folder of copies whose original the user saves again just as its copy is read back.</summary>
    private sealed class SavesWhileItIsRead(string original) : Adapters.IDestinationAdapter
    {
        private readonly Adapters.MirrorFolderAdapter _inner = new();

        public DestinationKind Kind => DestinationKind.MirrorFolder;

        public Task<Adapters.DestinationProbe> ProbeAsync(Destination destination, CancellationToken ct) => _inner.ProbeAsync(destination, ct);

        public Task<Adapters.SourceBinding> BindAsync(Destination destination, Sample sample, string scratchDirectory, Adapters.BindPurpose purpose,
            IReadOnlyCollection<string> alsoRead, CancellationToken ct) => _inner.BindAsync(destination, sample, scratchDirectory, purpose, alsoRead, ct);

        public Task<Adapters.CopyLookup> LocateAsync(Adapters.SourceBinding binding, string relativePath, CancellationToken ct) => _inner.LocateAsync(binding, relativePath, ct);

        public async Task<Adapters.ReadBack> ReadBackAsync(Adapters.SourceBinding binding, Adapters.CopyLookup copy, string scratchPath, CancellationToken ct)
        {
            var read = await _inner.ReadBackAsync(binding, copy, scratchPath, ct);
            if (string.Equals(Path.GetFileName(copy.Path), Path.GetFileName(original), StringComparison.Ordinal))
            {
                File.WriteAllText(original, "document 3, saved again while the drill was reading its copy");
            }

            return read;
        }
    }

    [Fact]
    public async Task ADrillThatReadNothingBack_IsNeverCalledARestore_AndSaysWhy()
    {
        using var tree = new TempTree();
        using var backup = new TempTree();
        using var scratch = new TempTree();
        // Every file larger than a drill reads, as in a folder of videos: nothing is sampled.
        for (var i = 0; i < 3; i++) tree.Write($"videos/{i}.mp4", new string('v', 2000), DateTimeOffset.UtcNow.AddDays(-3));
        tree.MirrorTo(backup.Root);

        var report = await new DrillRunner().RunAsync(new DrillPlan
        {
            Sources = [Source.FromPath(tree.Root, "Videos")],
            Destinations = [Destination.MirrorFolder(backup.Root, "Drive")],
            ScratchDirectory = scratch.Root,
            Sampling = Everything with { MaxFileBytes = 1000 },
        });

        Assert.Equal(Verdict.NothingToCheck, Assert.Single(report.Destinations).Score.Verdict);
        Assert.Equal("Nothing could be checked", Verdicts.Headline(report.Destinations));
        Assert.Equal("No file could be read: 3 are too large.", Verdicts.NothingChecked(report.Sources));
        Assert.Contains("<h1><span class=\"dot none-fill\" aria-hidden=\"true\"></span>Nothing could be checked</h1>", HtmlReport.Render(report));
        Assert.Equal(string.Empty, Verdicts.NothingChecked([report.Sources[0] with { Sampled = 1 }]));
        using (new InKorean())
        {
            Assert.Equal("확인할 수 있는 파일이 없었습니다", Verdicts.Headline(report.Destinations));
            Assert.Equal("읽을 수 있는 파일이 없었습니다. 제외한 파일: 너무 큰 파일 3개.", Verdicts.NothingChecked(report.Sources));
        }
    }

    [Fact]
    public void ADrillRecordedBefore1_0_SaysNothingOfMissingFolders_AndReadsAsHavingFoundThem()
    {
        const string old = """{"sourceId":"a","name":"Docs","rootPath":"/home/max/docs","population":3,"populationBytes":30,"sampled":3,"sampledBytes":30,"skippedLargeFiles":0,"skippedChurningFiles":0}""";

        var summary = JsonSerializer.Deserialize<SourceSummary>(old, BoatdrillJson.Options)!;

        Assert.False(summary.Missing);
        Assert.Equal(3, summary.Population);
        Assert.Equal(string.Empty, Verdicts.MissingSources([]));
    }

    [Fact]
    public async Task UnreachableDestination_SaysSo()
    {
        using var tree = new TempTree();
        using var scratch = new TempTree();
        WriteDocuments(tree, 3, DateTimeOffset.UtcNow.AddDays(-2));

        var report = await new DrillRunner().RunAsync(Plan(scratch, Source.FromPath(tree.Root, "Docs"), Destination.MirrorFolder(scratch.At("unplugged"), "USB drive")));

        var result = Assert.Single(report.Destinations);
        Assert.False(result.Reachable);
        Assert.Equal(Verdict.Unreachable, result.Score.Verdict);
        Assert.Equal(0, result.Score.Total);
        Assert.StartsWith("USB drive could not be reached:", result.Sentence);
        Assert.Equal(Verdict.Unreachable, report.Worst);
    }

    [Fact]
    public async Task SourceNotInDestination_IsNamedInTheSentence()
    {
        using var tree = new TempTree();
        using var backup = new TempTree();
        using var scratch = new TempTree();
        WriteDocuments(tree, 10, DateTimeOffset.UtcNow.AddDays(-2));
        backup.Write("something-else.txt", "unrelated");

        var report = await new DrillRunner().RunAsync(Plan(scratch, Source.FromPath(tree.Root, "Docs"), Destination.MirrorFolder(backup.Root, "Drive")));

        var result = Assert.Single(report.Destinations);
        Assert.Equal(10, result.Missing);
        Assert.Equal(Verdict.WouldNotRestore, result.Score.Verdict);
        Assert.Contains($"No copy of Docs was found under {backup.Root}", result.Sentence);
    }

    [Fact]
    public async Task ScratchFolderIsRemovedAfterTheDrill()
    {
        using var tree = new TempTree();
        using var backup = new TempTree();
        using var scratch = new TempTree();
        WriteDocuments(tree, 5, DateTimeOffset.UtcNow.AddDays(-2));
        tree.MirrorTo(backup.Root);

        await new DrillRunner().RunAsync(Plan(scratch, Source.FromPath(tree.Root), Destination.MirrorFolder(backup.Root)));

        Assert.Empty(Directory.GetDirectories(scratch.Root, "boatdrill-*"));
    }

    [Fact]
    public async Task Progress_ReachesTheLastFile()
    {
        using var tree = new TempTree();
        using var backup = new TempTree();
        using var scratch = new TempTree();
        WriteDocuments(tree, 8, DateTimeOffset.UtcNow.AddDays(-2));
        tree.MirrorTo(backup.Root);
        var seen = new List<DrillProgress>();

        await new DrillRunner().RunAsync(Plan(scratch, Source.FromPath(tree.Root), Destination.MirrorFolder(backup.Root)), new SynchronousProgress(seen));

        Assert.Contains(seen, p => p.FilesTotal == 8 && p.FilesDone == 8);
    }

    [Fact]
    public async Task History_RoundTripsAndFeedsReachability()
    {
        using var tree = new TempTree();
        using var backup = new TempTree();
        using var scratch = new TempTree();
        WriteDocuments(tree, 5, DateTimeOffset.UtcNow.AddDays(-2));
        tree.MirrorTo(backup.Root);
        var destination = Destination.MirrorFolder(backup.Root, "Drive");
        var store = new HistoryStore(scratch.At(Path.Combine("data", "history.jsonl")));

        var report = await new DrillRunner().RunAsync(Plan(scratch, Source.FromPath(tree.Root), destination));
        await store.AppendAsync(report);
        File.AppendAllText(store.FilePath, "{ torn line");

        var read = Assert.Single(store.ReadAll());
        Assert.Equal(report.Id, read.Id);
        Assert.Equal(report.Destinations[0].Sentence, read.Destinations[0].Sentence);
        Assert.Equal(report.Destinations[0].Score, read.Destinations[0].Score);
        Assert.Equal(1.0, store.Reachability(destination.Id));
        Assert.Null(store.Reachability("never-seen"));
        Assert.Equal(report.Id, store.Latest()?.Id);
    }

    [Fact]
    public async Task Findings_AreCappedButCountsAreNot()
    {
        using var tree = new TempTree();
        using var backup = new TempTree();
        using var scratch = new TempTree();
        WriteDocuments(tree, DrillRunner.MaxFindingsPerDestination + 20, DateTimeOffset.UtcNow.AddDays(-2));
        tree.Write("docs/x-kept.txt", "kept", DateTimeOffset.UtcNow.AddDays(-2));
        backup.Write("docs/x-kept.txt", "kept", DateTimeOffset.UtcNow.AddDays(-2));

        var report = await new DrillRunner().RunAsync(Plan(scratch, Source.FromPath(tree.Root), Destination.MirrorFolder(backup.Root)));

        var result = Assert.Single(report.Destinations);
        Assert.Equal(DrillRunner.MaxFindingsPerDestination + 20, result.Missing);
        Assert.Equal(DrillRunner.MaxFindingsPerDestination, result.Findings.Count);
        Assert.Equal(1, result.Verified);
    }

    [Fact]
    public async Task Findings_ListTheWorstFirst()
    {
        using var tree = new TempTree();
        using var backup = new TempTree();
        using var scratch = new TempTree();
        var when = DateTimeOffset.UtcNow.AddDays(-2);
        WriteDocuments(tree, 6, when);
        tree.MirrorTo(backup.Root, skip: ["docs/0.txt"]);
        var damaged = backup.At(Path.Combine("docs", "5.txt"));
        File.WriteAllText(damaged, "document X");
        File.SetLastWriteTimeUtc(damaged, when.UtcDateTime);

        var report = await new DrillRunner().RunAsync(Plan(scratch, Source.FromPath(tree.Root), Destination.MirrorFolder(backup.Root)));

        var findings = Assert.Single(report.Destinations).Findings;
        Assert.Equal([FileOutcome.Corrupt, FileOutcome.Missing], findings.Select(f => f.Outcome));
    }

    [Fact]
    public void Reachability_BlendsHistoryWithNow()
    {
        Assert.Equal(1.0, DrillRunner.Reachability(null, true));
        Assert.Equal(0.0, DrillRunner.Reachability(null, false));
        Assert.Equal(0.7, DrillRunner.Reachability(1.0, false), 3);
        Assert.Equal(0.3, DrillRunner.Reachability(0.0, true), 3);
    }

    [Fact]
    public async Task ABackupWhoseFilesCannotBeListed_SaysSo_RatherThanThatTheFilesHaveNoCopy()
    {
        using var tree = new TempTree();
        using var scratch = new TempTree();
        tree.Write("a.txt", "a", DateTimeOffset.UtcNow.AddDays(-5));

        var report = await new DrillRunner([new CannotList()]).RunAsync(
            Plan(scratch, Source.FromPath(tree.Root, "Docs"), Destination.Rclone("remote:backup", name: "Remote")));

        var d = Assert.Single(report.Destinations);
        Assert.Equal((0, 1), (d.Missing, d.Errors));
        Assert.Equal("Remote would not restore", Verdicts.Headline(report.Destinations));
        Assert.EndsWith($"0 of 1 sampled files read back correctly and 1 could not be read. Docs: {CannotList.Problem}.", d.Sentence);
        Assert.DoesNotContain("no copy", d.Sentence);
    }

    [Fact]
    public async Task ADestinationThatFails_CostsOnlyItsOwnResult_AndPassesNowhere()
    {
        using var tree = new TempTree();
        using var backup = new TempTree();
        using var scratch = new TempTree();
        WriteDocuments(tree, 5, DateTimeOffset.UtcNow.AddDays(-2));
        tree.MirrorTo(backup.Root);

        var report = await new DrillRunner([new Adapters.MirrorFolderAdapter(), new Breaks(DestinationKind.Rclone)]).RunAsync(
            Plan(scratch, Source.FromPath(tree.Root, "Docs"), Destination.Rclone("remote:backup", name: "Cloud"), Destination.MirrorFolder(backup.Root, "Drive")));

        // The drive, checked after the error, has its result as ever.
        Assert.Equal(["Cloud", "Drive"], report.Destinations.Select(d => d.DestinationName));
        var drive = report.Destinations[1];
        Assert.Equal((Verdict.WouldRestore, 5), (drive.Score.Verdict, drive.Verified));

        var cloud = report.Destinations[0];
        Assert.Equal((Verdict.CouldNotCheck, false, 0), (cloud.Score.Verdict, cloud.Reachable, cloud.Sampled));
        Assert.Equal("There is not enough space on the disk", cloud.Failure);
        Assert.Equal("Cloud could not be checked: There is not enough space on the disk. Whether it would restore is not known; the next drill will check it again.", cloud.Sentence);

        // Not a pass as the drill's verdict, in a notification or in the report.
        Assert.Equal(Verdict.CouldNotCheck, report.Worst);
        Assert.Equal("Cloud could not be checked", Verdicts.Headline(report.Destinations));
        var notice = NotificationPolicy.For(report);
        Assert.NotNull(notice);
        Assert.Equal("Cloud could not be checked", notice.Title);
        Assert.Equal("There is not enough space on the disk. Whether it would restore is not known; the next drill will check it again.", notice.Text);
        var html = HtmlReport.Render(report);
        Assert.Contains("<h1><span class=\"dot bad-fill\" aria-hidden=\"true\"></span>Cloud could not be checked</h1>", html);
        Assert.Contains("<div class=\"score bad\"><span class=\"total\">–</span><span class=\"verdict\">could not be checked</span>", html);
        TempTree.AssertEmpty(scratch.Root);

        // The history keeps why, so the sentence is written again from it, in Korean too.
        var read = JsonSerializer.Deserialize<DrillReport>(JsonSerializer.Serialize(report, BoatdrillJson.Options), BoatdrillJson.Options)!.Destinations[0];
        Assert.Equal((Verdict.CouldNotCheck, cloud.Failure), (read.Score.Verdict, read.Failure));
        using (new InKorean())
        {
            Assert.Equal("Cloud를 확인하지 못했습니다: There is not enough space on the disk. 복원할 수 있는지는 아직 알 수 없습니다. 다음 복원 훈련 때 다시 확인합니다.",
                Verdicts.Sentence(read, report.FinishedUtc));
            Assert.Equal("Cloud를 확인하지 못했습니다", Verdicts.Headline([read]));
            Assert.Equal("확인 못 함", Verdicts.Words(read.Score.Verdict));
        }
    }

    [Fact]
    public async Task AProbeThatFails_OrTimesOutOnItsOwn_LeavesThatDestinationUnchecked()
    {
        using var tree = new TempTree();
        using var backup = new TempTree();
        using var scratch = new TempTree();
        WriteDocuments(tree, 3, DateTimeOffset.UtcNow.AddDays(-2));
        tree.MirrorTo(backup.Root);
        var failures = new List<string>();

        // A time limit of the adapter's own is not the drill being stopped.
        var report = await new DrillRunner([new Adapters.MirrorFolderAdapter(), new Breaks(DestinationKind.Kopia, whenProbed: new TaskCanceledException("The request timed out"))],
            failed: (destination, ex) => failures.Add($"{destination.Name}: {ex.GetType().Name}")).RunAsync(
            Plan(scratch, Source.FromPath(tree.Root, "Docs"), Destination.MirrorFolder(backup.Root, "Drive"), Destination.Kopia("/srv/kopia", "secret", name: "Repository")));

        Assert.Equal([Verdict.WouldRestore, Verdict.CouldNotCheck], report.Destinations.Select(d => d.Score.Verdict));
        Assert.StartsWith("Repository could not be checked: The request timed out. ", report.Destinations[1].Sentence);
        Assert.Equal(["Repository: TaskCanceledException"], failures);
    }

    [Fact]
    public async Task AStoppedDrill_Stops_WhateverErrorTheStopLeftAToolToFailWith()
    {
        using var tree = new TempTree();
        using var scratch = new TempTree();
        WriteDocuments(tree, 3, DateTimeOffset.UtcNow.AddDays(-2));
        using var stop = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DrillRunner([new Breaks(DestinationKind.Rclone, beforeReadBack: stop.Cancel)]).RunAsync(
            Plan(scratch, Source.FromPath(tree.Root, "Docs"), Destination.Rclone("remote:backup", name: "Cloud")), ct: stop.Token));
        TempTree.AssertEmpty(scratch.Root);
    }

    /// <summary>
    /// A backup that answers and lists its copies, then fails as no adapter foresaw: when it is probed, or when a copy
    /// is read back, as when the scratch disk fills while a tool writes the copy into it.
    /// </summary>
    private sealed class Breaks(DestinationKind kind, Exception? whenProbed = null, Action? beforeReadBack = null) : Adapters.IDestinationAdapter
    {
        public DestinationKind Kind => kind;

        public Task<Adapters.DestinationProbe> ProbeAsync(Destination destination, CancellationToken ct) =>
            whenProbed is not null ? throw whenProbed : Task.FromResult(new Adapters.DestinationProbe(true, "answered", null));

        public Task<Adapters.SourceBinding> BindAsync(Destination destination, Sample sample, string scratchDirectory, Adapters.BindPurpose purpose,
            IReadOnlyCollection<string> alsoRead, CancellationToken ct) =>
            Task.FromResult(new Adapters.SourceBinding(destination, sample.Source, MirrorLayout.Auto, "remote:backup", sample.Files.Count, sample.Files.Count));

        public Task<Adapters.CopyLookup> LocateAsync(Adapters.SourceBinding binding, string relativePath, CancellationToken ct) =>
            Task.FromResult(new Adapters.CopyLookup(Adapters.CopyState.Found, "remote:backup/" + relativePath, 10, null, null));

        public Task<Adapters.ReadBack> ReadBackAsync(Adapters.SourceBinding binding, Adapters.CopyLookup copy, string scratchPath, CancellationToken ct)
        {
            beforeReadBack?.Invoke();
            throw new IOException("There is not enough space on the disk.");
        }
    }

    private sealed class SynchronousProgress(List<DrillProgress> sink) : IProgress<DrillProgress>
    {
        public void Report(DrillProgress value) => sink.Add(value);
    }
}
