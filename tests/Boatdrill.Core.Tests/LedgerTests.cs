using Boatdrill.Core.Drill;
using Boatdrill.Core.History;
using Boatdrill.Core.Sampling;
using Boatdrill.Core.Scoring;
using Boatdrill.Core.Verification;

namespace Boatdrill.Core.Tests;

public sealed class LedgerTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private static readonly SampleOptions Everything = new() { SampleSize = 1000, Seed = 1, ChurnWindow = TimeSpan.FromHours(-1), PerFolderFloor = 0 };

    private static LedgerEntry Entry(string path, DateTimeOffset verified, long length = 100, string source = "s1", string destination = "d1") =>
        new(destination, source, path, "/copies/" + path, length, verified, "hash of " + path, length, verified, verified);

    private static Dictionary<string, IReadOnlyCollection<string>> Sampled(string source, params string[] paths) => new() { [source] = paths };

    [Fact]
    public void Due_TakesRotFirst_ThenTheLongestUnchecked_WithinTheLimits()
    {
        var now = Start.AddDays(30);
        var ledger = new HashLedger(
        [
            Entry("old.txt", Start),
            Entry("older.txt", Start.AddDays(-5)),
            Entry("rotting.txt", Start.AddDays(20)) with { RotSeenUtc = Start.AddDays(25) },
            Entry("fresh.txt", now.AddHours(-2)),
            Entry("huge.bin", Start.AddDays(-9), length: HashLedger.RecheckBytes),
            Entry("sampled.txt", Start.AddDays(-8)),
            Entry("other-source.txt", Start.AddDays(-7), source: "s2"),
            Entry("other-destination.txt", Start.AddDays(-7), destination: "d2"),
        ]);

        var due = ledger.Due("d1", Sampled("s1", "sampled.txt"), now);

        // huge.bin is oldest but would take the whole read budget once rotting.txt is in; the smaller ones still fit.
        Assert.Equal(["rotting.txt", "older.txt", "old.txt"], due.Select(e => e.RelativePath));
    }

    [Fact]
    public void Due_StopsAtTheRecheckLimit_OldestFirst()
    {
        var ledger = new HashLedger(Enumerable.Range(0, 40).Select(i => Entry($"{i}.txt", Start.AddMinutes(-i))));

        var due = ledger.Due("d1", Sampled("s1"), Start.AddDays(3));

        Assert.Equal(HashLedger.RechecksPerDrill, due.Count);
        Assert.Equal("39.txt", due[0].RelativePath);
        Assert.DoesNotContain(due, e => e.RelativePath == "0.txt");
    }

    [Fact]
    public void PastTheLimit_TheLongestUncheckedIsForgotten_ButNeverRot()
    {
        var ledger = new HashLedger();
        ledger.Record(Entry("rotting.txt", Start.AddDays(-1)) with { RotSeenUtc = Start });
        for (var i = 0; i < HashLedger.MaxEntriesPerDestination; i++) ledger.Record(Entry($"{i}.txt", Start.AddMinutes(i)));

        Assert.Equal(HashLedger.MaxEntriesPerDestination, ledger.Count("d1"));
        Assert.Contains(ledger.Entries, e => e.RelativePath == "rotting.txt");
        Assert.DoesNotContain(ledger.Entries, e => e.RelativePath == "0.txt");

        ledger.Record(Entry("1.txt", Start.AddDays(2)));
        Assert.Equal(HashLedger.MaxEntriesPerDestination, ledger.Count("d1"));
    }

    [Fact]
    public void Keep_ForgetsWhatIsNoLongerSetUp()
    {
        var ledger = new HashLedger([Entry("a.txt", Start), Entry("b.txt", Start, source: "s2"), Entry("c.txt", Start, destination: "d2")]);

        ledger.Keep(["d1"], ["s1"]);

        Assert.Equal("a.txt", Assert.Single(ledger.Entries).RelativePath);
        Assert.Equal(0, ledger.Count("d2"));
    }

    [Fact]
    public async Task TheStore_RoundTrips_AndADamagedFileStartsEmpty()
    {
        using var folder = new TempTree();
        var store = new LedgerStore(folder.At("ledger.json"));
        var entries = new[] { Entry("a.txt", Start), Entry("b.txt", Start.AddDays(1)) with { RotSeenUtc = Start.AddDays(2) } };

        await store.SaveAsync(new HashLedger(entries));
        var loaded = store.Load();

        Assert.Equal(entries.OrderBy(e => e.RelativePath), loaded.Entries.OrderBy(e => e.RelativePath));
        Assert.False(File.Exists(folder.At("ledger.json.tmp")));

        File.WriteAllText(store.FilePath, "{\"version\":1,\"entries\":[{\"destinationId\":");
        Assert.Empty(store.Load().Entries);
        Assert.Empty(new LedgerStore(folder.At("missing.json")).Load().Entries);
    }

    // ---------------- drills with a ledger, against real folders ----------------

    private static async Task<DestinationResult> DrillAsync(TempTree source, TempTree mirror, HashLedger ledger, TestClock clock, params string[] excludes)
    {
        using var scratch = new TempTree();
        var report = await new DrillRunner(clock: clock, ledger: ledger).RunAsync(new DrillPlan
        {
            Sources = [Source.FromPath(source.Root, "Docs") with { Excludes = excludes }],
            Destinations = [Destination.MirrorFolder(mirror.Root, "Drive")],
            ScratchDirectory = scratch.Root,
            Sampling = Everything,
        });
        return Assert.Single(report.Destinations);
    }

    private static (TempTree Source, TempTree Mirror) MirroredDocuments(int count)
    {
        var source = new TempTree();
        var mirror = new TempTree();
        for (var i = 0; i < count; i++) source.Write($"docs/{i}.txt", $"document {i}", Start.AddDays(-30));
        source.MirrorTo(mirror.Root);
        return (source, mirror);
    }

    /// <summary>Flips a bit in a copy and puts its write time back: what decay on a disk looks like.</summary>
    private static void Rot(string path)
    {
        var written = File.GetLastWriteTimeUtc(path);
        var bytes = File.ReadAllBytes(path);
        bytes[0] ^= 0x20;
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, written);
    }

    [Fact]
    public async Task CopiesVerifiedBefore_AreReadAgain_AndCountTowardsIntegrity()
    {
        var (source, mirror) = MirroredDocuments(10);
        using var _ = source;
        using var __ = mirror;
        var clock = new TestClock(Start);
        var ledger = new HashLedger();

        var first = await DrillAsync(source, mirror, ledger, clock);
        Assert.Equal(10, first.Verified);
        Assert.Equal(0, first.Rechecked);
        Assert.Equal(10, ledger.Count(first.DestinationId));

        clock.Now = Start.AddDays(7);
        var second = await DrillAsync(source, mirror, ledger, clock, "0.txt", "1.txt", "2.txt");

        Assert.Equal(7, second.Verified);
        Assert.Equal(3, second.Rechecked);
        Assert.Equal(0, second.Rotted);
        Assert.Equal(100, second.Score.Total);
        Assert.Contains("and 3 copies verified in earlier drills are unchanged", second.Sentence);
        Assert.All(ledger.Entries, e => Assert.Equal(clock.Now, e.VerifiedUtc));
    }

    [Fact]
    public async Task ACopyThatRots_IsCaughtByARecheck_UntilTheBackupReplacesIt()
    {
        var (source, mirror) = MirroredDocuments(10);
        using var _ = source;
        using var __ = mirror;
        var clock = new TestClock(Start);
        var ledger = new HashLedger();
        await DrillAsync(source, mirror, ledger, clock);

        Rot(mirror.At("docs/3.txt"));
        clock.Now = Start.AddDays(7);
        var second = await DrillAsync(source, mirror, ledger, clock, "3.txt");

        Assert.Equal(9, second.Verified);
        Assert.Equal(1, second.Rechecked);
        Assert.Equal(1, second.Rotted);
        var rot = Assert.Single(second.Findings);
        Assert.Equal(FileOutcome.Rotted, rot.Outcome);
        Assert.True(rot.Recheck);
        Assert.Equal("docs/3.txt", rot.RelativePath.Replace('\\', '/'));
        Assert.EndsWith("its bytes have changed since, though its size and date have not", rot.Detail);
        Assert.Equal(Scorer.CorruptionCap, second.Score.Total);
        Assert.Contains("1 of 1 copy verified in earlier drills has changed since without its size or date changing, which points to bit rot", second.Sentence);

        // An hour later the rot is not due by age, but rot is read again every drill until it is resolved.
        clock.Now = Start.AddDays(7).AddHours(1);
        var third = await DrillAsync(source, mirror, ledger, clock, "3.txt");
        Assert.Equal(1, third.Rotted);

        // The backup copies the file again: there is nothing left to compare, so the entry goes.
        source.Write("docs/3.txt", "document 3, saved again", Start.AddDays(8));
        source.MirrorTo(mirror.Root);
        clock.Now = Start.AddDays(9);
        var fourth = await DrillAsync(source, mirror, ledger, clock, "3.txt");
        Assert.Equal(0, fourth.Rotted);
        Assert.Equal(0, fourth.Rechecked);
        Assert.DoesNotContain(ledger.Entries, e => e.RelativePath.EndsWith("3.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACopyRemovedFromTheBackup_IsForgotten_AndACopyTooRecentWaits()
    {
        var (source, mirror) = MirroredDocuments(6);
        using var _ = source;
        using var __ = mirror;
        var clock = new TestClock(Start);
        var ledger = new HashLedger();
        await DrillAsync(source, mirror, ledger, clock);

        clock.Now = Start.AddHours(1);
        var soon = await DrillAsync(source, mirror, ledger, clock, "4.txt", "5.txt");
        Assert.Equal(0, soon.Rechecked);

        File.Delete(mirror.At("docs/5.txt"));
        clock.Now = Start.AddDays(2);
        var later = await DrillAsync(source, mirror, ledger, clock, "4.txt", "5.txt");

        Assert.Equal(1, later.Rechecked);
        Assert.Empty(later.Findings);
        Assert.Equal(5, ledger.Count(later.DestinationId));
    }

    [Fact]
    public void Rot_LowersIntegrity_AndCapsTheTotal()
    {
        var clean = Scorer.Score(new ScoreInputs(100, 100, 0, 0, 0, 0, 0, 1000, null, true, 1.0, Rechecked: 25));
        Assert.Equal(1.0, clean.Integrity);
        Assert.Equal(100, clean.Total);

        var rotten = Scorer.Score(new ScoreInputs(100, 100, 0, 0, 0, 0, 0, 1000, null, true, 1.0, Rechecked: 25, Rotted: 5));
        Assert.Equal(120.0 / 125, rotten.Integrity, 6);
        Assert.Equal(Scorer.CorruptionCap, rotten.Total);
        Assert.Equal(Verdict.NeedsAttention, rotten.Verdict);
    }
}
