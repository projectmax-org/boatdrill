using System.Text.Json;
using Boatdrill.Core;
using Boatdrill.Core.Drill;
using Boatdrill.Core.History;
using Boatdrill.Core.Scoring;
using Boatdrill.Core.Settings;
using Boatdrill.Core.Verification;

namespace Boatdrill.Capture;

/// <summary>
/// Settings and six weeks of drill history for the view capture: a drive that would restore, a NAS that
/// has been falling behind, and a travel SSD that is not plugged in. Scores and sentences come from the
/// real scorer and sentence writer, so the screenshots show what a user would see.
/// </summary>
internal static class SampleData
{
    private const int Population = 42_292;
    private const long PopulationBytes = 212L * 1024 * 1024 * 1024 + 300L * 1024 * 1024;

    public static void Write(string directory)
    {
        var settings = new AppSettings
        {
            Sources =
            [
                new SourceSetting { Id = "documents", Name = "Documents", Folder = @"C:\Users\you\Documents", Excludes = [.. AppSettings.DefaultExcludes] },
                new SourceSetting { Id = "pictures", Name = "Pictures", Folder = @"C:\Users\you\Pictures", Excludes = [.. AppSettings.DefaultExcludes] },
            ],
            Destinations =
            [
                new DestinationSetting { Id = "drive", Name = "Backup drive (E:)", Folder = @"E:\Backup" },
                new DestinationSetting { Id = "nas", Name = "Family NAS", Folder = @"\\nas\backups\laptop", Layout = MirrorLayout.RootFolder },
                new DestinationSetting { Id = "ssd", Name = "Travel SSD (F:)", Folder = @"F:\" },
            ],
            HasCompletedFirstRun = true,
            HasShownTrayHint = true,
        };

        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "settings.json"), JsonSerializer.Serialize(settings, BoatdrillJson.Indented));

        var history = new HistoryStore(Path.Combine(directory, "history.jsonl"));
        var now = DateTimeOffset.UtcNow;
        for (var weeksAgo = 5; weeksAgo >= 0; weeksAgo--)
        {
            history.AppendAsync(Drill(now.AddDays(-2 - 7 * weeksAgo).AddHours(-3), weeksAgo)).GetAwaiter().GetResult();
        }
    }

    private static DrillReport Drill(DateTimeOffset started, int weeksAgo)
    {
        var finished = started + TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(41);

        // The NAS stopped receiving new photos four weeks ago; the SSD went travelling three weeks ago.
        var nasMissing = weeksAgo >= 4 ? 2 : 150 - 30 * weeksAgo;
        var nasOutdated = weeksAgo >= 4 ? 1 : 20 - 4 * weeksAgo;
        var nasLag = weeksAgo >= 4 ? TimeSpan.FromHours(20) : TimeSpan.FromDays(23 - 7 * weeksAgo);

        return new DrillReport
        {
            Id = $"{started:yyyyMMdd-HHmmss}-sample",
            StartedUtc = started,
            FinishedUtc = finished,
            Trigger = weeksAgo == 0 ? DrillTrigger.Manual : DrillTrigger.Scheduled,
            Sources =
            [
                new SourceSummary("documents", "Documents", @"C:\Users\you\Documents", 18_412, 41L * 1024 * 1024 * 1024, 200, 190L * 1024 * 1024, 3, 12, finished.AddHours(-1)),
                new SourceSummary("pictures", "Pictures", @"C:\Users\you\Pictures", 23_880, 171L * 1024 * 1024 * 1024, 200, 322L * 1024 * 1024, 41, 0, finished.AddDays(-1)),
            ],
            Destinations =
            [
                Reached("drive", "Backup drive (E:)", @"E:\Backup", finished, verified: 397, outdated: 3, missing: 0,
                    lag: TimeSpan.FromDays(1), reachability: 1.0, bytesPerSecond: 96d * 1024 * 1024, DriveFindings()),
                Reached("nas", "Family NAS", @"\\nas\backups\laptop", finished, verified: 400 - nasMissing - nasOutdated, outdated: nasOutdated, missing: nasMissing,
                    lag: nasLag, reachability: 0.9, bytesPerSecond: 31d * 1024 * 1024, NasFindings(nasMissing, nasOutdated)),
                weeksAgo >= 4
                    ? Reached("ssd", "Travel SSD (F:)", @"F:\", finished, verified: 400, outdated: 0, missing: 0,
                        lag: null, reachability: 1.0, bytesPerSecond: 380d * 1024 * 1024, [])
                    : Unreached("ssd", "Travel SSD (F:)", @"F:\", finished, reachability: weeksAgo == 3 ? 0.8 : 0.5),
            ],
        };
    }

    private static DestinationResult Reached(string id, string name, string root, DateTimeOffset at, int verified, int outdated, int missing,
        TimeSpan? lag, double reachability, double bytesPerSecond, IReadOnlyList<FileFinding> findings)
    {
        var sampled = verified + outdated + missing;
        var result = new DestinationResult
        {
            DestinationId = id,
            DestinationName = name,
            RootPath = root,
            Reachable = true,
            ProbeDetail = root + " is reachable",
            Bindings =
            [
                new BoundSource("documents", "Documents", MirrorLayout.RootFolder, Path.Combine(root, "Documents"), 25, 25),
                new BoundSource("pictures", "Pictures", MirrorLayout.RootFolder, Path.Combine(root, "Pictures"), 25, 25),
            ],
            Sampled = sampled,
            Verified = verified,
            Outdated = outdated,
            Missing = missing,
            Population = Population,
            PopulationBytes = PopulationBytes,
            BytesRead = 512L * 1024 * 1024,
            ReadTime = TimeSpan.FromSeconds(512d * 1024 * 1024 / bytesPerSecond),
            BytesPerSecond = bytesPerSecond,
            EstimatedFullRestore = TimeSpan.FromSeconds(PopulationBytes / bytesPerSecond),
            BehindSince = lag is { } behind ? at - behind : null,
            Lag = lag,
            Score = Scorer.Score(new ScoreInputs(sampled, verified, outdated, 0, missing, 0, 0, Population, lag, true, reachability)),
            Sentence = string.Empty,
            Findings = findings,
        };

        return result with { Sentence = Verdicts.Sentence(result, at) };
    }

    private static DestinationResult Unreached(string id, string name, string root, DateTimeOffset at, double reachability)
    {
        var result = new DestinationResult
        {
            DestinationId = id,
            DestinationName = name,
            RootPath = root,
            Reachable = false,
            ProbeDetail = root + " was not found",
            Population = Population,
            PopulationBytes = PopulationBytes,
            Score = Scorer.Score(new ScoreInputs(0, 0, 0, 0, 0, 0, 0, 0, null, false, reachability)),
            Sentence = string.Empty,
        };

        return result with { Sentence = Verdicts.Sentence(result, at) };
    }

    private static IReadOnlyList<FileFinding> DriveFindings()
    {
        var edited = DateTimeOffset.UtcNow.AddDays(-3);
        return
        [
            Outdated(@"Documents\Notes\Q4 planning.docx", edited, 1),
            Outdated(@"Documents\Taxes\2026 return draft.xlsx", edited.AddHours(-5), 1),
            Outdated(@"Pictures\Screenshots\Screenshot 2026-10-26 101422.png", edited.AddHours(-9), 1),
        ];
    }

    private static IReadOnlyList<FileFinding> NasFindings(int missing, int outdated)
    {
        var findings = new List<FileFinding>();
        var taken = new DateTimeOffset(2026, 10, 4, 15, 12, 0, TimeSpan.Zero);
        for (var i = 0; i < Math.Min(missing, 12); i++)
        {
            var file = $@"Pictures\2026\10\IMG_{4821 + i * 7}.jpg";
            findings.Add(new FileFinding(file, FileOutcome.Missing, 4_200_000 + i * 91_000, taken.AddHours(i * 9), null, null, @"expected at \\nas\backups\laptop\" + file));
        }

        if (outdated > 0)
        {
            findings.Add(Outdated(@"Documents\Family\Insurance renewal.pdf", taken.AddDays(2), 21));
            findings.Add(Outdated(@"Documents\Taxes\2026 return draft.xlsx", taken.AddDays(9), 23));
        }

        return findings;
    }

    private static FileFinding Outdated(string path, DateTimeOffset written, int daysBehind) =>
        new(path, FileOutcome.Outdated, 120_000, written, 118_000, written.AddDays(-daysBehind),
            $"the copy was written {daysBehind} {(daysBehind == 1 ? "day" : "days")} before the original");
}
