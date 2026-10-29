using Boatdrill.Core.Drill;
using Boatdrill.Core.History;
using Boatdrill.Core.Scoring;

namespace Boatdrill.Core.Tests;

public sealed class HistoryStoreTests
{
    private static DrillReport Report(int n, bool reachable = true) => new()
    {
        Id = $"drill-{n:000}",
        StartedUtc = DateTimeOffset.UnixEpoch.AddDays(n),
        FinishedUtc = DateTimeOffset.UnixEpoch.AddDays(n).AddMinutes(3),
        Trigger = DrillTrigger.Scheduled,
        Sources = [],
        Destinations =
        [
            new DestinationResult
            {
                DestinationId = "drive",
                DestinationName = "Drive",
                RootPath = "/mnt/drive",
                Reachable = reachable,
                Score = new ReadinessScore(1, 1, 1, 1, reachable ? 100 : 0, 0, reachable ? Verdict.WouldRestore : Verdict.Unreachable),
                Sentence = "s",
            },
        ],
    };

    [Fact]
    public async Task ReadRecent_ReturnsTheLastDrillsOldestFirst()
    {
        using var data = new TempTree();
        var store = new HistoryStore(data.At("history.jsonl"));
        for (var i = 1; i <= 5; i++) await store.AppendAsync(Report(i));

        Assert.Equal(["drill-004", "drill-005"], store.ReadRecent(2).Select(r => r.Id));
        Assert.Equal(5, store.ReadAll().Count);
        Assert.Equal("drill-005", store.Latest()?.Id);
        Assert.Empty(store.ReadRecent(0));
    }

    [Fact]
    public async Task Latest_SkipsATornLastLine()
    {
        using var data = new TempTree();
        var store = new HistoryStore(data.At("history.jsonl"));
        await store.AppendAsync(Report(1));
        File.AppendAllText(store.FilePath, "{\"id\":\"half");

        Assert.Equal("drill-001", store.Latest()?.Id);
    }

    [Fact]
    public async Task Reachability_CountsTheRecentWindow()
    {
        using var data = new TempTree();
        var store = new HistoryStore(data.At("history.jsonl"));
        await store.AppendAsync(Report(1, reachable: false));
        for (var i = 2; i <= 4; i++) await store.AppendAsync(Report(i));

        Assert.Equal(0.75, store.Reachability("drive"));
        Assert.Equal(1.0, store.Reachability("drive", window: 3));
        Assert.Null(store.Reachability("elsewhere"));
    }

    [Fact]
    public async Task TrimIfLarge_KeepsTheNewestLines()
    {
        using var data = new TempTree();
        var store = new HistoryStore(data.At("history.jsonl"));
        for (var i = 1; i <= 10; i++) await store.AppendAsync(Report(i));

        store.TrimIfLarge(maxBytes: 100, keep: 3);

        Assert.Equal(["drill-008", "drill-009", "drill-010"], store.ReadAll().Select(r => r.Id));
        store.TrimIfLarge(maxBytes: long.MaxValue, keep: 1);
        Assert.Equal(3, store.ReadAll().Count);
    }

    [Fact]
    public void MissingFile_ReadsAsEmpty()
    {
        var store = new HistoryStore(Path.Combine(Path.GetTempPath(), "boatdrill-no-history", "history.jsonl"));

        Assert.Empty(store.ReadAll());
        Assert.Null(store.Latest());
        Assert.Null(store.Reachability("x"));
    }
}
