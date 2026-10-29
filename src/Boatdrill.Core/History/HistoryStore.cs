using System.Text.Json;
using Boatdrill.Core.Drill;

namespace Boatdrill.Core.History;

/// <summary>
/// Append-only drill history, one JSON report per line. Trends and reachability are read from it; the
/// hash ledger, which changes in place, has a file of its own (<see cref="LedgerStore"/>). A half-written last line, left by a crash or a power cut, is skipped, not
/// fatal. Past a few megabytes the oldest drills are dropped, keeping the most recent ones.
/// </summary>
public sealed class HistoryStore(string filePath)
{
    public const long TrimAboveBytes = 8L * 1024 * 1024;
    public const int KeepWhenTrimming = 200;

    private readonly Lock _gate = new();

    public string FilePath { get; } = filePath;

    public async Task AppendAsync(DrillReport report, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var line = JsonSerializer.Serialize(report, BoatdrillJson.Options) + "\n";
        await File.AppendAllTextAsync(FilePath, line, ct).ConfigureAwait(false);
        TrimIfLarge(TrimAboveBytes, KeepWhenTrimming);
    }

    public IReadOnlyList<DrillReport> ReadAll() => ReadRecent(int.MaxValue);

    /// <summary>The last <paramref name="count"/> readable drills, oldest first.</summary>
    public IReadOnlyList<DrillReport> ReadRecent(int count)
    {
        if (count <= 0 || !File.Exists(FilePath)) return [];

        string[] lines;
        lock (_gate)
        {
            lines = File.ReadLines(FilePath).Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(count).ToArray();
        }

        var reports = new List<DrillReport>(lines.Length);
        foreach (var line in lines)
        {
            try
            {
                var report = JsonSerializer.Deserialize<DrillReport>(line, BoatdrillJson.Options);
                if (report is not null) reports.Add(report);
            }
            catch (JsonException)
            {
                // A torn line; see the class summary.
            }
        }

        return reports;
    }

    /// <summary>The most recent readable drill. Looks a few lines back in case the last one is torn.</summary>
    public DrillReport? Latest() => ReadRecent(5).LastOrDefault();

    /// <summary>Share of the last <paramref name="window"/> drills in which the destination was reachable, or null without history.</summary>
    public double? Reachability(string destinationId, int window = 10)
    {
        var seen = ReadRecent(50)
            .SelectMany(r => r.Destinations)
            .Where(d => d.DestinationId == destinationId)
            .TakeLast(window)
            .ToList();
        return seen.Count == 0 ? null : (double)seen.Count(d => d.Reachable) / seen.Count;
    }

    internal void TrimIfLarge(long maxBytes, int keep)
    {
        lock (_gate)
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists || info.Length <= maxBytes) return;

            var kept = File.ReadLines(FilePath).Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(keep).ToArray();
            var temporary = FilePath + ".tmp";
            File.WriteAllLines(temporary, kept);
            File.Move(temporary, FilePath, overwrite: true);
        }
    }
}
