using System.Globalization;
using System.Text.Json;
using Boatdrill.Core;
using Boatdrill.Core.Drill;
using Boatdrill.Core.History;
using Boatdrill.Core.Reporting;
using Boatdrill.Core.Sampling;
using Boatdrill.Core.Scan;
using Boatdrill.Core.Scoring;
using Boatdrill.Core.Text;

namespace Boatdrill.Cli;

public static class Program
{
    private const string Usage = """
        boatdrill-cli drill     --source <folder> [--source <folder> ...] <destinations> [options]
        boatdrill-cli only-here --source <folder> [--source <folder> ...] <destinations> [--json]
        boatdrill-cli report    --history <file> [--html <file>]

        Destinations, as many as needed:
          --dest <folder>           a folder of plain copies: a drive, a NAS share, a synced cloud folder
          --file-history <folder>   a Windows File History drive, or its FileHistory folder
          --restic <repository>     a restic repository: a folder, or a URL such as sftp:host:/srv/restic
          --password-file <file>    the restic password, on the first line; without it, RESTIC_PASSWORD
          --restic-tool <path>      restic itself, when it is not on the PATH

        drill rehearses restoring a sample of files from every source out of every destination and says
        whether the backup would work. It never writes to a destination; it writes the scratch folder and,
        when asked, the history, the ledger and the canary files.

          --sample       files per source (200)          --cap-mb       most MB read per source (512)
          --seed         repeatable sample                --churn-hours  skip files written this recently (2)
          --scratch      where read-back lands (temp)     --history      append the report to this .jsonl file
          --ledger       hash ledger file: copies verified before are read again, to catch bit rot
          --canaries     keep a canary file in each source, to show how far behind each backup is
          --html         also write the drill as a one-page HTML report to this file
          --json         print the full report as JSON instead of text

        only-here looks every file in the sources up in every destination, by name and date, and lists the
        ones no backup holds a current copy of.

        report turns the latest drill in a history file into a one-page HTML report, with each backup's trend
        over the drills before it (boatdrill-report.html unless --html names the file).

        Exit codes: drill 0 when every destination would restore (fully or mostly), 1 otherwise; only-here 0
        when every file has a current copy, 1 otherwise; report 0 when it wrote the report; 2 for bad arguments.
        """;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }

        if (args[0] is "--version" or "version")
        {
            Console.WriteLine(typeof(Program).Assembly.GetName().Version?.ToString(3));
            return 0;
        }

        if (args[0] is not ("drill" or "only-here" or "report"))
        {
            Console.Error.WriteLine($"Unknown command '{args[0]}'.\n\n{Usage}");
            return 2;
        }

        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        return args[0] switch
        {
            "drill" => await DrillAsync(options),
            "only-here" => await OnlyHereAsync(options),
            _ => await ReportAsync(options),
        };
    }

    private static async Task<int> DrillAsync(Options options)
    {
        var store = options.History is null ? null : new HistoryStore(options.History);
        var ledgerStore = options.Ledger is null ? null : new LedgerStore(options.Ledger);
        var ledger = ledgerStore?.Load();
        var plan = new DrillPlan
        {
            Sources = options.Sources,
            Destinations = options.Destinations,
            ScratchDirectory = options.Scratch,
            Sampling = options.Sampling,
            UseCanaries = options.Canaries,
        };

        var runner = new DrillRunner(reachabilityHistory: id => store?.Reachability(id), ledger: ledger);
        var report = await runner.RunAsync(plan, options.Json ? null : new ConsoleProgress());
        if (!options.Json) Console.Error.WriteLine();
        if (store is not null) await store.AppendAsync(report);
        if (ledgerStore is not null && ledger is not null) await ledgerStore.SaveAsync(ledger);
        if (options.Html is not null)
        {
            await File.WriteAllTextAsync(options.Html, HtmlReport.Render(report, store?.ReadRecent(12), Version));
            if (!options.Json) Console.Error.WriteLine("Report written to " + Path.GetFullPath(options.Html));
        }

        if (options.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(report, BoatdrillJson.Indented));
        }
        else
        {
            Print(report);
        }

        return report.Worst <= Verdict.WouldMostlyRestore ? 0 : 1;
    }

    private static async Task<int> OnlyHereAsync(Options options)
    {
        var progress = options.Json ? null : new Progress<ScanProgress>(p =>
            Console.Error.Write("\r" + (p.FilesChecked > 0 ? $"{p.Stage}: {p.FilesChecked:N0} files, {p.OnlyHereFiles:N0} only here" : p.Stage + "...").PadRight(78)));
        var report = await new OnlyHereScanner().ScanAsync(options.Sources, options.Destinations, options.Scratch, progress);
        if (!options.Json) Console.Error.WriteLine();

        if (options.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(report, BoatdrillJson.Indented));
        }
        else
        {
            Print(report);
        }

        return report.Checked.Count > 0 && report.OnlyHereFiles == 0 ? 0 : 1;
    }

    private static async Task<int> ReportAsync(Options options)
    {
        var recent = new HistoryStore(options.History!).ReadRecent(12);
        if (recent.Count == 0)
        {
            Console.Error.WriteLine($"There is no drill in {options.History}.");
            return 2;
        }

        var path = Path.GetFullPath(options.Html ?? "boatdrill-report.html");
        await File.WriteAllTextAsync(path, HtmlReport.Render(recent[^1], recent, Version));
        Console.WriteLine($"Report of the drill on {recent[^1].StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm} written to {path}");
        return 0;
    }

    private static string? Version => typeof(Program).Assembly.GetName().Version?.ToString(3);

    private static void Print(DrillReport report)
    {
        Console.WriteLine($"Boatdrill drill at {report.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm}: " +
                          $"{Humanize.Plural(report.Sources.Count, "source")}, {Humanize.Plural(report.Destinations.Count, "destination")}, " +
                          $"{Humanize.Duration(report.Duration)}.");

        foreach (var source in report.Sources)
        {
            var skipped = source.SkippedLargeFiles + source.SkippedChurningFiles;
            Console.WriteLine($"  {source.Name}: {source.Population:N0} files, {Humanize.Bytes(source.PopulationBytes)}; " +
                              $"sampled {source.Sampled:N0} ({Humanize.Bytes(source.SampledBytes)})" +
                              (skipped > 0 ? $"; left out {source.SkippedLargeFiles:N0} large and {source.SkippedChurningFiles:N0} just-changed files" : string.Empty));
        }

        foreach (var d in report.Destinations)
        {
            Console.WriteLine();
            Console.WriteLine($"{d.DestinationName}  ({d.RootPath})");
            Console.WriteLine($"  {d.Sentence}");
            if (!d.Reachable) continue;

            var s = d.Score;
            Console.WriteLine($"  Score {s.Total}: coverage {Percent(s.Coverage)} (±{Percent(s.CoverageMargin)}), freshness {Percent(s.Freshness)}, " +
                              $"integrity {Percent(s.Integrity)}, reachability {Percent(s.Reachability)}");
            if (d.BytesPerSecond is { } speed)
            {
                Console.WriteLine($"  Read back {Humanize.Bytes(d.BytesRead)} at {Humanize.Bytes((long)speed)}/s");
            }

            if (d.Rechecked > 0)
            {
                Console.WriteLine($"  Read again {Humanize.Plural(d.Rechecked, "copy")} verified in earlier drills; {d.Rotted:N0} had changed");
            }

            if (d.CanaryCopiedUtc is { } copied)
            {
                Console.WriteLine($"  Canary: the newest stamp copied here is from {copied.ToLocalTime():yyyy-MM-dd HH:mm}");
            }

            foreach (var b in d.Bindings)
            {
                Console.WriteLine($"  {b.SourceName} looked up under {b.BasePath} ({b.Layout}, {b.ProbeHits}/{b.ProbeTried} probe hits)");
            }

            foreach (var f in d.Findings.Take(20))
            {
                Console.WriteLine($"  {f.Outcome,-11} {f.RelativePath}" + (f.Recheck ? "  [checked again]" : string.Empty) +
                                  (f.Detail is null ? string.Empty : $"  ({f.Detail})"));
            }

            if (d.Findings.Count > 20)
            {
                Console.WriteLine($"  ... and {d.Findings.Count - 20:N0} more; use --json for all of them");
            }
        }
    }

    private static void Print(OnlyHereReport report)
    {
        Console.WriteLine(report.Sentence);
        foreach (var unreachable in report.Unreachable) Console.WriteLine($"  Not looked in: {unreachable}");

        foreach (var s in report.Sources)
        {
            Console.WriteLine($"  {s.SourceName}: {s.Files:N0} files, {Humanize.Bytes(s.Bytes)}; " +
                              $"{s.NoCopyFiles:N0} with no copy ({Humanize.Bytes(s.NoCopyBytes)}), {s.OlderFiles:N0} changed since ({Humanize.Bytes(s.OlderBytes)})");
        }

        if (report.Folders.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Folders with the most only here:");
            foreach (var f in report.Folders.Take(15))
            {
                var folder = f.RelativePath.Length == 0 ? "(top folder)" : f.RelativePath;
                Console.WriteLine($"  {Humanize.Bytes(f.Bytes),10}  {f.SourceName}{Path.DirectorySeparatorChar}{folder}  ({Humanize.Plural(f.Files, "file")})");
            }
        }

        if (report.Largest.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Largest files only here:");
            foreach (var f in report.Largest.Take(15))
            {
                Console.WriteLine($"  {Humanize.Bytes(f.Length),10}  {f.SourceName}{Path.DirectorySeparatorChar}{f.RelativePath}" +
                                  (f.HasOlderCopy ? "  (an older copy exists)" : string.Empty));
            }
        }
    }

    private static string Percent(double share) => $"{Math.Round(share * 100)}%";

    private sealed record Options(
        IReadOnlyList<Source> Sources,
        IReadOnlyList<Destination> Destinations,
        SampleOptions Sampling,
        string Scratch,
        string? History,
        string? Ledger,
        bool Canaries,
        bool Json,
        string? Html)
    {
        public static Options Parse(string[] args)
        {
            var sources = new List<string>();
            var destinations = new List<(DestinationKind Kind, string Location)>();
            var sampleSize = 200;
            long capMb = 512;
            int? seed = null;
            var churnHours = 2.0;
            string? scratch = null, history = null, ledger = null, passwordFile = null, resticTool = null, html = null;
            bool canaries = false, json = false;

            for (var i = 1; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
                switch (args[i])
                {
                    case "--source": sources.Add(Next()); break;
                    case "--dest": destinations.Add((DestinationKind.MirrorFolder, Next())); break;
                    case "--file-history": destinations.Add((DestinationKind.FileHistory, Next())); break;
                    case "--restic": destinations.Add((DestinationKind.Restic, Next())); break;
                    case "--password-file": passwordFile = Next(); break;
                    case "--restic-tool": resticTool = Next(); break;
                    case "--sample": sampleSize = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--cap-mb": capMb = long.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--seed": seed = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--churn-hours": churnHours = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--scratch": scratch = Next(); break;
                    case "--history": history = Next(); break;
                    case "--ledger": ledger = Next(); break;
                    case "--canaries": canaries = true; break;
                    case "--json": json = true; break;
                    case "--html": html = Next(); break;
                    default: throw new ArgumentException($"Unknown option '{args[i]}'.\n\n{Usage}");
                }
            }

            if (args[0] == "report")
            {
                if (history is null) throw new ArgumentException("report needs --history <file>.\n\n" + Usage);
            }
            else if (sources.Count == 0 || destinations.Count == 0)
            {
                throw new ArgumentException("At least one --source and one destination are needed.\n\n" + Usage);
            }

            // The password never goes on the command line, where other programs can read it.
            var password = passwordFile is not null
                ? File.ReadLines(passwordFile).FirstOrDefault()?.TrimEnd('\r') ?? string.Empty
                : Environment.GetEnvironmentVariable("RESTIC_PASSWORD");

            return new Options(
                sources.Select(s => Source.FromPath(s)).ToList(),
                destinations.Select(d => d.Kind switch
                {
                    DestinationKind.FileHistory => Destination.FileHistory(d.Location),
                    DestinationKind.Restic => Destination.Restic(d.Location, password, toolPath: resticTool),
                    _ => Destination.MirrorFolder(d.Location),
                }).ToList(),
                new SampleOptions
                {
                    SampleSize = sampleSize,
                    ByteCap = capMb * 1024 * 1024,
                    Seed = seed,
                    ChurnWindow = TimeSpan.FromHours(churnHours),
                },
                scratch ?? Path.Combine(Path.GetTempPath(), "boatdrill"),
                history,
                ledger,
                canaries,
                json,
                html);
        }
    }

    /// <summary>Writes progress on the error stream, in place, so the report on stdout stays clean for pipes.</summary>
    private sealed class ConsoleProgress : IProgress<DrillProgress>
    {
        public void Report(DrillProgress p)
        {
            var line = p.FilesTotal > 0
                ? $"{p.Stage}: {p.FilesDone}/{p.FilesTotal} files, {Humanize.Bytes(p.BytesRead)}"
                : $"{p.Stage}...";
            Console.Error.Write("\r" + line.PadRight(78));
        }
    }
}
