using System.Globalization;
using System.Net;
using System.Text;
using Boatdrill.Core.Drill;
using Boatdrill.Core.Text;

namespace Boatdrill.Core.Reporting;

/// <summary>
/// A drill as one self-contained HTML page: the answer first, then a card per backup with its score, its trend,
/// its sentence, the four parts and the files that need a look. No scripts and no external files, light or dark
/// with the reader's settings, and fit to print. It names files in the user's folders but never holds their
/// contents, and says so at the foot.
/// </summary>
public static class HtmlReport
{
    /// <summary>Files listed per backup; the counts always cover every sampled file.</summary>
    public const int FindingsPerDestination = 25;

    /// <param name="recent">Earlier drills, oldest first, for each backup's trend; the drill itself may be among them.</param>
    /// <param name="version">The app's version, for the foot.</param>
    public static string Render(DrillReport report, IReadOnlyList<DrillReport>? recent = null, string? version = null, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        var korean = Language.IsKorean;
        var history = (recent ?? []).Where(r => r.Id != report.Id && r.StartedUtc < report.StartedUtc).Append(report).TakeLast(12).ToList();
        var headline = Verdicts.Headline(report.Destinations);
        var tone = Tone(report);

        var html = new StringBuilder(32 * 1024);
        html.Append("<!doctype html>\n<html lang=\"").Append(korean ? "ko" : "en").Append("\">\n<head>\n<meta charset=\"utf-8\">\n")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
            .Append("<meta name=\"generator\" content=\"Boatdrill").Append(version is null ? string.Empty : " " + E(version)).Append("\">\n")
            .Append("<title>").Append(E(Language.Pick("Boatdrill report, ", "Boatdrill 보고서, ") + Language.Date(report.StartedUtc))).Append("</title>\n")
            .Append("<style>").Append(Style).Append("</style>\n</head>\n<body>\n<main>\n");

        // The answer first.
        var reached = report.Destinations.Count(d => d.Reachable);
        var sampled = report.Sources.Sum(s => s.Sampled);
        html.Append("<header>\n<p class=\"eyebrow\">").Append(Language.Pick("Boatdrill drill report", "Boatdrill 복원 훈련 보고서")).Append("</p>\n")
            .Append("<h1><span class=\"dot ").Append(tone).Append("-fill\" aria-hidden=\"true\"></span>").Append(E(headline)).Append("</h1>\n")
            .Append("<p class=\"lead\">").Append(E(Lead(report, reached, sampled))).Append("</p>\n</header>\n");

        AppendSources(html, report);
        foreach (var destination in report.Destinations) AppendDestination(html, destination, history, report.FinishedUtc);

        var maker = "Boatdrill" + (version is null ? string.Empty : " " + version);
        html.Append("<footer>\n<p>")
            .Append(E(korean
                ? $"{Language.DateAndTime(at)}에 {Korean.Subject(maker)} 만들었습니다. Boatdrill은 백업을 읽기만 하며, 어떤 정보도 이 PC 밖으로 보내지 않습니다. " +
                  "이 보고서에는 폴더 속 파일의 이름은 들어 있지만 내용은 들어 있지 않습니다. 공유하기 전에 한 번 더 생각해 주세요."
                : $"Made by {maker} on {at.ToLocalTime().ToString("d MMMM yyyy 'at' HH:mm", CultureInfo.InvariantCulture)}. " +
                  "Boatdrill only ever reads from backups, and nothing leaves the PC it runs on. " +
                  "This report names files in your folders but holds none of their contents: think before you share it."))
            .Append("</p>\n</footer>\n</main>\n</body>\n</html>\n");
        return html.ToString();
    }

    /// <summary>When the drill ran, how, and what it read: "Drill on Monday …, on schedule, took 4 min. It read …".</summary>
    private static string Lead(DrillReport report, int reached, int sampled)
    {
        var scheduled = report.Trigger == DrillTrigger.Scheduled;
        var total = report.Destinations.Count;
        if (Language.IsKorean)
        {
            var when = $"{Language.DayDateAndTime(report.StartedUtc)}에 {(scheduled ? "예약대로" : "직접")} 실행한 복원 훈련으로, {Korean.Subject(Humanize.Duration(report.Duration))} 걸렸습니다. ";
            var backups = reached == total ? $"백업 {total:N0}개" : $"백업 {total:N0}개 중 {reached:N0}개";
            return when + (reached == 0
                ? "어떤 백업에도 연결하지 못했습니다."
                : $"표본 파일 {sampled:N0}개를 {backups}에서 다시 읽어 원본과 한 바이트도 빠짐없이 비교했습니다.");
        }

        return $"Drill on {Language.DayDateAndTime(report.StartedUtc)}, {(scheduled ? "on schedule" : "run by hand")}, took {Humanize.Duration(report.Duration)}. " +
               (reached == 0
                   ? "It could not reach any backup."
                   : $"It read {Humanize.Plural(sampled, "sampled file")} back from {(reached == total ? Humanize.Plural(reached, "backup") : $"{reached} of {Humanize.Plural(total, "backup")}")} and compared every byte with the original.");
    }

    private static void AppendSources(StringBuilder html, DrillReport report)
    {
        if (report.Sources.Count == 0) return;

        html.Append(Language.Pick(
                "<section class=\"card\">\n<h2>What is protected</h2>\n<table>\n<thead><tr><th>Folder</th><th class=\"num\">Files</th><th class=\"num\">Size</th>" +
                "<th class=\"num wide\">Sampled</th><th class=\"wide\">Left out</th></tr></thead>\n<tbody>\n",
                "<section class=\"card\">\n<h2>보호하는 폴더</h2>\n<table>\n<thead><tr><th>폴더</th><th class=\"num\">파일</th><th class=\"num\">크기</th>" +
                "<th class=\"num wide\">표본</th><th class=\"wide\">제외</th></tr></thead>\n<tbody>\n"));
        foreach (var s in report.Sources)
        {
            var left = new List<string>();
            if (s.SkippedLargeFiles > 0) left.Add(Language.Pick($"{s.SkippedLargeFiles:N0} too large", $"너무 큰 파일 {s.SkippedLargeFiles:N0}개"));
            if (s.SkippedChurningFiles > 0) left.Add(Language.Pick($"{s.SkippedChurningFiles:N0} changed in the last hours", $"최근 몇 시간 사이에 바뀐 파일 {s.SkippedChurningFiles:N0}개"));
            html.Append("<tr><td><strong>").Append(E(s.Name)).Append("</strong><br><span class=\"muted small\">").Append(E(s.RootPath)).Append("</span></td>")
                .Append("<td class=\"num\">").Append(s.Population.ToString("N0", CultureInfo.InvariantCulture)).Append("</td>")
                .Append("<td class=\"num\">").Append(E(Humanize.Bytes(s.PopulationBytes))).Append("</td>")
                .Append("<td class=\"num wide\">").Append(s.Sampled.ToString("N0", CultureInfo.InvariantCulture)).Append("</td>")
                .Append("<td class=\"muted wide\">").Append(E(left.Count == 0 ? Language.Pick("nothing", "없음") : string.Join(", ", left))).Append("</td></tr>\n");
        }

        html.Append("</tbody>\n</table>\n</section>\n");
    }

    /// <param name="finished">When the drill finished: the sentence is written again from the result's numbers, as of then.</param>
    private static void AppendDestination(StringBuilder html, DestinationResult r, IReadOnlyList<DrillReport> history, DateTimeOffset finished)
    {
        var tone = Verdicts.Tone(r.Score.Verdict);
        html.Append("<section class=\"card\">\n<div class=\"head\">\n")
            .Append("<div class=\"badge ").Append(tone).Append("-soft ").Append(tone).Append("\" aria-hidden=\"true\">").Append(Icon(r.Kind)).Append("</div>\n")
            .Append("<div class=\"who\"><h2>").Append(E(r.DestinationName)).Append("</h2><p class=\"muted small\">").Append(E(Where(r))).Append("</p></div>\n")
            .Append(Trend(r.DestinationId, history))
            .Append("<div class=\"score ").Append(tone).Append("\"><span class=\"total\">").Append(r.Reachable ? r.Score.Total.ToString(CultureInfo.InvariantCulture) : "–")
            .Append("</span><span class=\"verdict\">").Append(E(Verdicts.Words(r.Score.Verdict))).Append("</span></div>\n</div>\n")
            .Append("<p class=\"sentence\">").Append(E(Verdicts.Sentence(r, finished))).Append("</p>\n");

        var parts = ScoreParts.Describe(r);
        if (parts.Count > 0)
        {
            html.Append("<div class=\"parts\">\n");
            foreach (var part in parts)
            {
                html.Append("<div class=\"part\"><span class=\"muted small\">").Append(E(part.Name)).Append("</span><strong>").Append(E(part.Value))
                    .Append("</strong><span class=\"muted small\">").Append(E(part.Detail)).Append("</span></div>\n");
            }

            html.Append("</div>\n");
        }

        var korean = Language.IsKorean;
        var facts = new List<string>();
        if (r.BytesPerSecond is { } speed)
        {
            facts.Add(korean
                ? $"{Korean.Object(Humanize.Bytes(r.BytesRead))} 초당 {Humanize.Bytes((long)speed)} 속도로 다시 읽음"
                : $"Read back {Humanize.Bytes(r.BytesRead)} at {Humanize.Bytes((long)speed)}/s");
        }

        foreach (var b in r.Bindings) facts.Add(korean ? $"{b.SourceName}: {b.BasePath}에서 찾음" : $"{b.SourceName} found under {b.BasePath}");
        if (r.CanaryCopiedUtc is { } copied) facts.Add(Language.Pick("Newest canary stamp copied: ", "복사된 가장 최근 카나리아 기록: ") + Language.DateAndTime(copied));
        if (facts.Count > 0)
        {
            html.Append("<ul class=\"facts muted small\">");
            foreach (var fact in facts) html.Append("<li>").Append(E(fact)).Append("</li>");
            html.Append("</ul>\n");
        }

        if (r.Findings.Count > 0)
        {
            html.Append("<table class=\"findings\">\n<colgroup><col class=\"c-outcome\"><col class=\"c-file\"><col></colgroup>\n")
                .Append(Language.Pick("<thead><tr><th>Outcome</th><th>File</th><th>What was found</th></tr></thead>\n<tbody>\n",
                    "<thead><tr><th>결과</th><th>파일</th><th>확인한 내용</th></tr></thead>\n<tbody>\n"));
            foreach (var f in r.Findings.Take(FindingsPerDestination))
            {
                var outcomeTone = Verdicts.Tone(f.Outcome);
                html.Append("<tr><td><span class=\"tag ").Append(outcomeTone).Append("-soft ").Append(outcomeTone).Append("\">").Append(E(Verdicts.Words(f.Outcome))).Append("</span></td>")
                    .Append("<td class=\"path\">").Append(E(f.RelativePath)).Append("</td>")
                    .Append("<td class=\"muted\">").Append(E((f.Recheck ? Language.Pick("Read again: ", "다시 읽음: ") : string.Empty) + (f.Detail ?? string.Empty))).Append("</td></tr>\n");
            }

            html.Append("</tbody>\n</table>\n");
            var notVerified = r.Sampled - r.Verified + r.Findings.Count(f => f.Recheck);
            var listed = Math.Min(r.Findings.Count, FindingsPerDestination);
            if (notVerified > listed)
            {
                html.Append("<p class=\"muted small\">")
                    .Append(E(Language.Pick($"The first {listed:N0} of {notVerified:N0} files that did not verify.", $"확인되지 않은 파일 {notVerified:N0}개 중 처음 {listed:N0}개입니다.")))
                    .Append("</p>\n");
            }
        }

        html.Append("</section>\n");
    }

    /// <summary>Where the backup is, as shown on the dashboard.</summary>
    private static string Where(DestinationResult r) => Destination.Describe(r.Kind, r.RootPath);

    /// <summary>The colour family of the headline: the worst of the backups.</summary>
    private static string Tone(DrillReport report) =>
        report.Destinations.Count == 0 ? "none"
        : report.Destinations.Any(d => Notifications.NotificationPolicy.IsProblem(d.Score.Verdict)) ? "bad"
        : report.Destinations.Any(d => d.Score.Verdict == Scoring.Verdict.WouldMostlyRestore) ? "fair"
        : "good";

    /// <summary>The score in the last few drills, with a dot on the baseline for each that could not reach the backup.</summary>
    private static string Trend(string destinationId, IReadOnlyList<DrillReport> history)
    {
        var results = history.Select(d => d.Destinations.FirstOrDefault(r => r.DestinationId == destinationId)).OfType<DestinationResult>().ToList();
        if (results.Count < 2) return string.Empty;

        const double width = 120, height = 32;
        var points = new List<string>();
        var dots = new StringBuilder();
        for (var i = 0; i < results.Count; i++)
        {
            var x = 3 + i * (width - 6) / (results.Count - 1);
            if (results[i].Reachable)
            {
                points.Add(string.Create(CultureInfo.InvariantCulture, $"{x:0.#},{2 + (height - 4) * (1 - results[i].Score.Total / 100.0):0.#}"));
            }
            else
            {
                dots.Append(string.Create(CultureInfo.InvariantCulture, $"<circle cx=\"{x:0.#}\" cy=\"{height - 3}\" r=\"2.4\" class=\"miss\"/>"));
            }
        }

        var scores = string.Join(", ", results.Select(r => r.Reachable ? r.Score.Total.ToString(CultureInfo.InvariantCulture) : Language.Pick("not reached", "연결 안 됨")));
        var words = E(Language.Pick($"Score in the last {results.Count} drills: ", $"최근 복원 훈련 {results.Count}번의 점수: ") + scores);
        var svg = new StringBuilder()
            .Append(string.Create(CultureInfo.InvariantCulture, $"<svg class=\"trend\" viewBox=\"0 0 {width} {height}\" width=\"{width}\" height=\"{height}\" "))
            .Append("role=\"img\" aria-label=\"").Append(words).Append("\"><title>").Append(words).Append("</title>")
            .Append(string.Create(CultureInfo.InvariantCulture, $"<line x1=\"0\" x2=\"{width}\" y1=\"{height - 2}\" y2=\"{height - 2}\" class=\"base\"/>"));
        if (points.Count >= 2) svg.Append("<polyline points=\"").Append(string.Join(' ', points)).Append("\" class=\"line\"/>");
        return svg.Append(dots).Append("</svg>\n").ToString();
    }

    /// <summary>The app's own icons, stroke-drawn on a 24-unit grid.</summary>
    private static string Icon(DestinationKind kind)
    {
        var path = kind switch
        {
            DestinationKind.FileHistory => "M8.4 3.6 H17.6 A1.6 1.6 0 0 1 19.2 5.2 V14.4 M5.6 6.8 H14.8 A1.6 1.6 0 0 1 16.4 8.4 V18.8 A1.6 1.6 0 0 1 14.8 20.4 H5.6 A1.6 1.6 0 0 1 4 18.8 V8.4 A1.6 1.6 0 0 1 5.6 6.8 Z M10.2 11 V14.2 L12.2 15.4",
            DestinationKind.Restic => "M4.8 6.4 C4.8 3.9 19.2 3.9 19.2 6.4 C19.2 8.9 4.8 8.9 4.8 6.4 Z M4.8 6.4 V17.6 C4.8 20.1 19.2 20.1 19.2 17.6 V6.4 M4.8 12 C4.8 14.5 19.2 14.5 19.2 12",
            DestinationKind.Rclone => CloudIcon,
            DestinationKind.Archive => ArchiveIcon,
            _ => "M3.2 13.6 L5.7 5.9 A1.8 1.8 0 0 1 7.4 4.6 H16.6 A1.8 1.8 0 0 1 18.3 5.9 L20.8 13.6 M3.2 13.6 H20.8 V18 A1.6 1.6 0 0 1 19.2 19.6 H4.8 A1.6 1.6 0 0 1 3.2 18 Z M16.6 16.6 H16.61 M13.6 16.6 H13.61",
        };
        return $"<svg viewBox=\"0 0 24 24\" width=\"20\" height=\"20\"><path d=\"{path}\"/></svg>";
    }

    /// <summary>A cloud, for a remote; the same figure as Icon.Cloud in the app.</summary>
    internal const string CloudIcon = "M7.2 18.6 H17.4 A3.9 3.9 0 0 0 17.9 10.8 A5.6 5.6 0 0 0 7.1 9.7 A4.5 4.5 0 0 0 7.2 18.6 Z";

    /// <summary>A box, for archives; the same figure as Icon.Archive in the app.</summary>
    internal const string ArchiveIcon = "M3.6 5 H20.4 V8.6 H3.6 Z M5 8.6 V18.4 A1.6 1.6 0 0 0 6.6 20 H17.4 A1.6 1.6 0 0 0 19 18.4 V8.6 M10 12 H14";

    private static string E(string text) => WebUtility.HtmlEncode(text);

    private const string Style = """

        :root {
          color-scheme: light dark;
          --bg: #f4f5f7; --surface: #ffffff; --surface-alt: #f8f9fb; --border: #e2e5ea;
          --text: #15171c; --muted: #5f6675; --accent: #2563eb;
          --good: #0f7b55; --good-soft: #e2f3eb; --fair: #9a5b00; --fair-soft: #fbefd9;
          --bad: #c42b3c; --bad-soft: #fbe6e9; --none: #5f6675; --none-soft: #eef0f3;
        }
        @media (prefers-color-scheme: dark) {
          :root {
            --bg: #121418; --surface: #1b1e24; --surface-alt: #22262d; --border: #30353e;
            --text: #eceef2; --muted: #9aa1ae; --accent: #4f8ef7;
            --good: #3ccf91; --good-soft: #173a2c; --fair: #f2b14c; --fair-soft: #3d2f17;
            --bad: #ff6b7d; --bad-soft: #43202a; --none: #9aa1ae; --none-soft: #2a2e36;
          }
        }
        @media print {
          :root {
            color-scheme: light;
            --bg: #ffffff; --surface: #ffffff; --surface-alt: #f8f9fb; --border: #d9dde3;
            --text: #15171c; --muted: #5f6675; --accent: #2563eb;
            --good: #0f7b55; --good-soft: #e2f3eb; --fair: #9a5b00; --fair-soft: #fbefd9;
            --bad: #c42b3c; --bad-soft: #fbe6e9; --none: #5f6675; --none-soft: #eef0f3;
          }
          main { padding: 0; }
          .card { break-inside: avoid; }
        }
        * { box-sizing: border-box; }
        body { margin: 0; background: var(--bg); color: var(--text);
               font: 14px/1.55 "Segoe UI", "Malgun Gothic", "Apple SD Gothic Neo", "Noto Sans KR", system-ui, -apple-system, "Helvetica Neue", Arial, sans-serif; }
        html:lang(ko) body { word-break: keep-all; overflow-wrap: anywhere; }
        main { max-width: 900px; margin: 0 auto; padding: 36px 20px 48px; }
        header { margin: 0 4px 22px; }
        .eyebrow { margin: 0 0 6px; font-size: 11.5px; font-weight: 600; letter-spacing: .08em; text-transform: uppercase; color: var(--muted); }
        h1 { display: flex; align-items: center; gap: 12px; margin: 0; font-size: 26px; line-height: 1.25; }
        h2 { margin: 0; font-size: 17px; line-height: 1.3; }
        .card > h2 { margin-bottom: 10px; }
        .lead { margin: 8px 0 0; color: var(--muted); }
        .dot { flex: none; width: 11px; height: 11px; border-radius: 50%; }
        .card { margin: 0 0 16px; padding: 20px 22px; background: var(--surface); border: 1px solid var(--border); border-radius: 14px; }
        .head { display: grid; grid-template-columns: auto minmax(0, 1fr) auto auto; gap: 16px; align-items: center; }
        .who p { margin: 3px 0 0; overflow-wrap: anywhere; }
        .badge { display: grid; place-items: center; width: 42px; height: 42px; border-radius: 10px; }
        .badge svg, .trend { fill: none; stroke-linecap: round; stroke-linejoin: round; }
        .badge svg { stroke: currentColor; stroke-width: 1.7; }
        .trend .line { stroke: var(--accent); stroke-width: 1.8; }
        .trend .base { stroke: var(--border); stroke-width: 1; }
        .trend .miss { fill: var(--bad); stroke: none; }
        .score { display: flex; flex-direction: column; align-items: flex-end; }
        .score .total { font-size: 32px; font-weight: 650; line-height: 1; }
        .score .verdict { font-size: 12px; font-weight: 600; }
        .sentence { margin: 14px 0 0; }
        .parts { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 8px; margin-top: 14px; }
        .part { display: flex; flex-direction: column; padding: 9px 12px; background: var(--surface-alt); border: 1px solid var(--border); border-radius: 10px; }
        .part strong { font-size: 18px; line-height: 1.35; }
        .facts { margin: 12px 0 0; padding: 0; list-style: none; }
        .facts li { overflow-wrap: anywhere; }
        table { width: 100%; margin-top: 14px; border-collapse: collapse; font-size: 13px; }
        th { padding: 6px 8px; border-bottom: 1px solid var(--border); color: var(--muted); font-size: 12px; font-weight: 600; text-align: left; }
        td { padding: 7px 8px; border-bottom: 1px solid var(--border); vertical-align: top; }
        tr:last-child td { border-bottom: none; }
        .card > table:first-of-type { margin-top: 4px; }
        .num { text-align: right; font-variant-numeric: tabular-nums; white-space: nowrap; }
        .findings { table-layout: fixed; }
        .findings td { overflow-wrap: anywhere; }
        .c-outcome { width: 96px; }
        .c-file { width: 42%; }
        .tag { display: inline-block; padding: 1px 8px; border-radius: 6px; font-size: 11.5px; font-weight: 600; white-space: nowrap; }
        .muted { color: var(--muted); }
        .small { font-size: 12px; }
        .good { color: var(--good); } .good-soft { background: var(--good-soft); } .good-fill { background: var(--good); }
        .fair { color: var(--fair); } .fair-soft { background: var(--fair-soft); } .fair-fill { background: var(--fair); }
        .bad { color: var(--bad); } .bad-soft { background: var(--bad-soft); } .bad-fill { background: var(--bad); }
        .none { color: var(--none); } .none-soft { background: var(--none-soft); } .none-fill { background: var(--none); }
        footer { margin: 24px 4px 0; color: var(--muted); font-size: 12px; }
        @media (max-width: 640px) {
          main { padding: 24px 12px 36px; }
          .card { padding: 16px; }
          .parts { grid-template-columns: repeat(2, minmax(0, 1fr)); }
          .head { grid-template-columns: auto minmax(0, 1fr); gap: 12px; }
          .trend, .wide { display: none; }
          th, td { padding-left: 4px; padding-right: 4px; }
          .score { grid-column: 1 / -1; flex-direction: row; align-items: baseline; gap: 8px; }
          .findings, .findings tbody { display: block; }
          .findings thead, .findings colgroup { display: none; }
          .findings tr { display: grid; grid-template-columns: auto minmax(0, 1fr); gap: 2px 10px; padding: 8px 0; border-bottom: 1px solid var(--border); }
          .findings tr:last-child { border-bottom: none; }
          .findings td { padding: 0; border: none; }
          .findings td:last-child { grid-column: 2; }
        }

        """;
}
