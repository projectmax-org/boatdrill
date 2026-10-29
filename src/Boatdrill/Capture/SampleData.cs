using System.Text.Json;
using Boatdrill.Core;
using Boatdrill.Core.Drill;
using Boatdrill.Core.History;
using Boatdrill.Core.Scan;
using Boatdrill.Core.Scoring;
using Boatdrill.Core.Settings;
using Boatdrill.Core.Verification;
using Boatdrill.Platform;

namespace Boatdrill.Capture;

/// <summary>
/// Settings, six weeks of drill history and a scan for the view capture: a drive that would restore, a NAS
/// that has been falling behind and has a rotting photo, a restic repository offsite, and a File History
/// drive that is not plugged in. Scores and sentences come from the real scorer and sentence writers, so
/// the screenshots show what a user would see.
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
                new DestinationSetting { Id = "offsite", Name = "Offsite repository", Kind = DestinationKind.Restic, Folder = "sftp:backup@cabin.example:/srv/restic", Secret = Protect("sample password") },
                new DestinationSetting { Id = "history", Name = "File History (F:)", Kind = DestinationKind.FileHistory, Folder = @"F:\" },
                new DestinationSetting { Id = "gdrive", Name = "Google Drive", Kind = DestinationKind.Rclone, Folder = "gdrive:Backups/Laptop" },
            ],
            UseCanaries = true,
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

        File.WriteAllText(Path.Combine(directory, "only-here.json"), JsonSerializer.Serialize(Scan(now.AddDays(-1)), BoatdrillJson.Options));
        File.WriteAllText(Path.Combine(directory, "rehearsal.json"), JsonSerializer.Serialize(Rehearsal(now.AddDays(-3)), BoatdrillJson.Options));
    }

    /// <summary>The password as the app would keep it; plain where DPAPI is missing, which the app also accepts.</summary>
    private static string Protect(string password)
    {
        try
        {
            return Dpapi.Protect(password);
        }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            return password;
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
                    lag: TimeSpan.FromDays(1), reachability: 1.0, bytesPerSecond: 96d * 1024 * 1024, DriveFindings(),
                    rechecked: weeksAgo < 5 ? 25 : 0, canaryCopied: finished.AddHours(-20)),
                Reached("nas", "Family NAS", @"\\nas\backups\laptop", finished, verified: 400 - nasMissing - nasOutdated, outdated: nasOutdated, missing: nasMissing,
                    lag: nasLag, reachability: 0.9, bytesPerSecond: 31d * 1024 * 1024, NasFindings(nasMissing, nasOutdated, rotting: weeksAgo <= 1),
                    rechecked: weeksAgo < 5 ? 25 : 0, rotted: weeksAgo <= 1 ? 1 : 0),
                Reached("offsite", "Offsite repository", "sftp:backup@cabin.example:/srv/restic", finished, verified: 400, outdated: 0, missing: 0,
                    lag: null, reachability: weeksAgo == 2 ? 0.8 : 1.0, bytesPerSecond: 6.5 * 1024 * 1024, [],
                    rechecked: weeksAgo < 5 ? 25 : 0, canaryCopied: finished.AddHours(-11), kind: DestinationKind.Restic),
                weeksAgo >= 4
                    ? Reached("history", "File History (F:)", @"F:\", finished, verified: 400, outdated: 0, missing: 0,
                        lag: null, reachability: 1.0, bytesPerSecond: 380d * 1024 * 1024, [], kind: DestinationKind.FileHistory)
                    : Unreached("history", "File History (F:)", @"F:\", finished, reachability: weeksAgo == 3 ? 0.8 : 0.5, kind: DestinationKind.FileHistory),
            ],
        };
    }

    private static DestinationResult Reached(string id, string name, string root, DateTimeOffset at, int verified, int outdated, int missing,
        TimeSpan? lag, double reachability, double bytesPerSecond, IReadOnlyList<FileFinding> findings,
        int rechecked = 0, int rotted = 0, DateTimeOffset? canaryCopied = null, DestinationKind kind = DestinationKind.MirrorFolder)
    {
        var sampled = verified + outdated + missing;
        var restic = kind == DestinationKind.Restic;
        var result = new DestinationResult
        {
            DestinationId = id,
            DestinationName = name,
            Kind = kind,
            RootPath = root,
            Reachable = true,
            ProbeDetail = restic ? root + " holds 214 snapshots" : root + " is reachable",
            Bindings = restic
                ?
                [
                    new BoundSource("documents", "Documents", MirrorLayout.FullPath, $"{root}, snapshot 4f1c2a9e of {at.AddHours(-11):d MMMM yyyy, HH:mm} UTC", 25, 25),
                    new BoundSource("pictures", "Pictures", MirrorLayout.FullPath, $"{root}, snapshot 4f1c2a9e of {at.AddHours(-11):d MMMM yyyy, HH:mm} UTC", 25, 25),
                ]
                :
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
            Rechecked = rechecked,
            Rotted = rotted,
            CanaryCopiedUtc = canaryCopied,
            Score = Scorer.Score(new ScoreInputs(sampled, verified, outdated, 0, missing, 0, 0, Population, lag, true, reachability, rechecked, rotted)),
            Sentence = string.Empty,
            Findings = findings,
        };

        return result with { Sentence = Verdicts.Sentence(result, at) };
    }

    private static DestinationResult Unreached(string id, string name, string root, DateTimeOffset at, double reachability, DestinationKind kind)
    {
        var result = new DestinationResult
        {
            DestinationId = id,
            DestinationName = name,
            Kind = kind,
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

    private static IReadOnlyList<FileFinding> NasFindings(int missing, int outdated, bool rotting)
    {
        var findings = new List<FileFinding>();
        if (rotting)
        {
            var shot = new DateTimeOffset(2019, 7, 14, 17, 3, 0, TimeSpan.Zero);
            findings.Add(new FileFinding(@"Pictures\2019\Summer\IMG_2231.jpg", FileOutcome.Rotted, 3_912_441, shot, 3_912_441, shot,
                "verified on 2 September 2026; its bytes have changed since, though its size and date have not") { Recheck = true });
        }

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

    /// <summary>A scan from yesterday: new photos the NAS never got, and a few documents changed since every copy.</summary>
    private static OnlyHereReport Scan(DateTimeOffset at)
    {
        const long MB = 1024 * 1024;
        var report = new OnlyHereReport
        {
            StartedUtc = at.AddMinutes(-2).AddSeconds(-14),
            FinishedUtc = at,
            Checked = ["Backup drive (E:)", "Family NAS", "Offsite repository"],
            Unreachable = [@"File History (F:): F:\ was not found"],
            Sources =
            [
                new OnlyHereSource("documents", "Documents", @"C:\Users\you\Documents")
                {
                    Files = 18_412, Bytes = 41L * 1024 * MB, NoCopyFiles = 3, NoCopyBytes = 18 * MB, OlderFiles = 9, OlderBytes = 7 * MB,
                },
                new OnlyHereSource("pictures", "Pictures", @"C:\Users\you\Pictures")
                {
                    Files = 23_880, Bytes = 171L * 1024 * MB, NoCopyFiles = 64, NoCopyBytes = 1_310 * MB, OlderFiles = 0, OlderBytes = 0,
                },
            ],
            Folders =
            [
                new OnlyHereFolder("Pictures", @"2026\10", 58, 1_190 * MB),
                new OnlyHereFolder("Pictures", @"Screenshots", 6, 120 * MB),
                new OnlyHereFolder("Documents", @"Projects\Kitchen", 4, 16 * MB),
                new OnlyHereFolder("Documents", @"Taxes", 5, 6 * MB),
                new OnlyHereFolder("Documents", "", 3, 3 * MB),
            ],
            Largest =
            [
                new OnlyHereFile("Pictures", @"2026\10\VID_20261024_181205.mp4", 612 * MB, at.AddDays(-4), false),
                new OnlyHereFile("Pictures", @"2026\10\VID_20261019_120033.mp4", 344 * MB, at.AddDays(-9), false),
                new OnlyHereFile("Pictures", @"Screenshots\Recording 2026-10-22.mp4", 98 * MB, at.AddDays(-6), false),
                new OnlyHereFile("Documents", @"Projects\Kitchen\Survey scan.pdf", 12 * MB, at.AddDays(-2), false),
                new OnlyHereFile("Documents", @"Taxes\2026 return draft.xlsx", 2 * MB, at.AddDays(-1), true),
            ],
            Sentence = string.Empty,
        };

        return report with { Sentence = OnlyHereScanner.Sentence(report) };
    }

    /// <summary>Three days ago, all of Pictures restored from the drive: three files did not come back as they are now.</summary>
    private static RehearsalResult Rehearsal(DateTimeOffset at)
    {
        const long GB = 1024L * 1024 * 1024;
        var shot = new DateTimeOffset(2026, 10, 24, 18, 12, 0, TimeSpan.Zero);
        var result = new RehearsalResult
        {
            SourceId = "pictures",
            SourceName = "Pictures",
            SourceRoot = @"C:\Users\you\Pictures",
            DestinationId = "drive",
            DestinationName = "Backup drive (E:)",
            RootPath = @"E:\Backup",
            StartedUtc = at.AddMinutes(-47),
            FinishedUtc = at,
            Reachable = true,
            ProbeDetail = @"E:\Backup is reachable",
            Files = 23_880,
            Bytes = 171 * GB,
            Verified = 23_877,
            Missing = 2,
            Outdated = 1,
            BytesRestored = 171 * GB - 9 * 1024 * 1024,
            RestoreTime = TimeSpan.FromMinutes(41),
            Findings =
            [
                new FileFinding(@"2026\10\IMG_20261024_181205.jpg", FileOutcome.Missing, 4_212_331, shot, null, null, @"expected at E:\Backup\Pictures\2026\10\IMG_20261024_181205.jpg"),
                new FileFinding(@"2026\10\IMG_20261024_181247.jpg", FileOutcome.Missing, 4_390_118, shot.AddSeconds(42), null, null, @"expected at E:\Backup\Pictures\2026\10\IMG_20261024_181247.jpg"),
                Outdated(@"Screenshots\Screenshot 2026-10-26 101422.png", shot.AddDays(2), 1),
            ],
            Sentence = string.Empty,
        };

        return result with { Sentence = RehearsalRunner.Sentence(result) };
    }

    private static FileFinding Outdated(string path, DateTimeOffset written, int daysBehind) =>
        new(path, FileOutcome.Outdated, 120_000, written, 118_000, written.AddDays(-daysBehind),
            $"the copy was written {daysBehind} {(daysBehind == 1 ? "day" : "days")} before the original");
}
