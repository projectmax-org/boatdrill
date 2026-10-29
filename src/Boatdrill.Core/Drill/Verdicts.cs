using System.Text;
using Boatdrill.Core.Notifications;
using Boatdrill.Core.Scoring;
using Boatdrill.Core.Text;
using Boatdrill.Core.Verification;

namespace Boatdrill.Core.Drill;

/// <summary>Turns a result into the one plain sentence the app shows under the score.</summary>
public static class Verdicts
{
    public static string Sentence(DestinationResult r, DateTimeOffset now)
    {
        var name = r.DestinationName;
        switch (r.Score.Verdict)
        {
            case Verdict.Unreachable:
                var fix = r.Kind switch
                {
                    DestinationKind.Restic => "Check the repository in Settings",
                    DestinationKind.Rclone => "Check the remote in Settings",
                    _ => "Plug it in or check the share",
                };
                return $"{name} could not be reached: {r.ProbeDetail}. {fix}, then run a drill.";
            case Verdict.NothingToCheck:
                return $"{name} is reachable, but no files were sampled, so there is nothing to verify yet.";
        }

        var lead = r.Score.Verdict switch
        {
            Verdict.WouldRestore => $"{name} would restore.",
            Verdict.WouldMostlyRestore => $"{name} would mostly restore.",
            Verdict.NeedsAttention => $"{name} needs attention.",
            _ => $"{name} would not restore.",
        };

        var facts = new List<string> { $"{r.Verified:N0} of {r.Sampled:N0} sampled files read back correctly" };
        if (r.Missing > 0) facts.Add($"{r.Missing:N0} {(r.Missing == 1 ? "has" : "have")} no copy");
        if (r.Outdated > 0) facts.Add($"{r.Outdated:N0} {(r.Outdated == 1 ? "is" : "are")} older than the original");
        if (r.Corrupt > 0) facts.Add($"{r.Corrupt:N0} differ{(r.Corrupt == 1 ? "s" : "")} from the original without being older, which points to corruption");
        if (r.Placeholder > 0) facts.Add($"{r.Placeholder:N0} exist{(r.Placeholder == 1 ? "s" : "")} only in the cloud, not on this disk");
        if (r.Errors > 0) facts.Add($"{r.Errors:N0} could not be read");
        if (r.Rotted > 0)
        {
            facts.Add($"{r.Rotted:N0} of {r.Rechecked:N0} {(r.Rechecked == 1 ? "copy" : "copies")} verified in earlier drills " +
                      $"{(r.Rotted == 1 ? "has" : "have")} changed since without {(r.Rotted == 1 ? "its" : "their")} size or date changing, which points to bit rot");
        }
        else if (r.Rechecked > 0)
        {
            facts.Add($"{r.Rechecked:N0} {(r.Rechecked == 1 ? "copy" : "copies")} verified in earlier drills {(r.Rechecked == 1 ? "is" : "are")} unchanged");
        }

        var text = new StringBuilder(lead).Append(' ').Append(Join(facts)).Append('.');

        var lost = r.Bindings.Where(b => b.ProbeTried > 0 && b.ProbeHits == 0).ToList();
        var unexplained = lost.Where(b => b.Problem is null).Select(b => b.SourceName).ToList();
        if (unexplained.Count > 0)
        {
            text.Append($" No copy of {Join(unexplained)} was found under {r.RootPath}.");
        }

        foreach (var explained in lost.Where(b => b.Problem is not null))
        {
            text.Append($" {explained.SourceName}: {explained.Problem}.");
        }

        if (r.BehindSince is { } since)
        {
            text.Append($" The oldest change not yet copied here is from {since.ToLocalTime():d MMMM yyyy} ({Humanize.Age(now - since)} ago).");
        }

        if (r.CanaryBehindSince is not null)
        {
            text.Append(r.CanaryCopiedUtc is { } copied
                ? $" The newest canary stamp copied here is from {copied.ToLocalTime():d MMMM yyyy, HH:mm}; the later ones have not been copied."
                : " No copy of the canary file was found here.");
        }

        if (r.EstimatedFullRestore is { } estimate && r.PopulationBytes > 0)
        {
            text.Append($" A full restore of {Humanize.Bytes(r.PopulationBytes)} would take about {Humanize.Duration(estimate)} at the speed measured.");
        }

        return text.ToString();
    }

    /// <summary>The answer first, for a set of results: how the backups stand, in a few words.</summary>
    public static string Headline(IReadOnlyList<DestinationResult> results)
    {
        if (results.Count == 0) return "No backups were checked";

        var problems = results.Where(r => NotificationPolicy.IsProblem(r.Score.Verdict)).ToList();
        return problems.Count switch
        {
            0 when results.All(r => r.Score.Verdict != Verdict.WouldMostlyRestore) => results.Count == 1 ? "Your backup would restore" : "Your backups would restore",
            0 => results.Count == 1 ? "Your backup would mostly restore" : "Your backups would mostly restore",
            1 => $"{problems[0].DestinationName} {Words(problems[0].Score.Verdict)}",
            _ => $"{problems.Count} backups need attention",
        };
    }

    /// <summary>A verdict in words, as it follows a destination's name.</summary>
    public static string Words(Verdict verdict) => verdict switch
    {
        Verdict.WouldRestore => "would restore",
        Verdict.WouldMostlyRestore => "would mostly restore",
        Verdict.NeedsAttention => "needs attention",
        Verdict.WouldNotRestore => "would not restore",
        Verdict.Unreachable => "could not be reached",
        _ => "nothing to check",
    };

    /// <summary>A file's outcome as a short label.</summary>
    public static string Words(FileOutcome outcome) => outcome switch
    {
        FileOutcome.Corrupt => "Corrupt",
        FileOutcome.Rotted => "Changed",
        FileOutcome.Error => "Unreadable",
        FileOutcome.Missing => "Missing",
        FileOutcome.Outdated => "Behind",
        FileOutcome.Placeholder => "Cloud only",
        _ => "Verified",
    };

    /// <summary>The colour family a verdict is shown in: good, fair, bad or none.</summary>
    public static string Tone(Verdict verdict) => verdict switch
    {
        Verdict.WouldRestore => "good",
        Verdict.WouldMostlyRestore => "fair",
        Verdict.NeedsAttention or Verdict.WouldNotRestore or Verdict.Unreachable => "bad",
        _ => "none",
    };

    public static string Tone(FileOutcome outcome) => outcome switch
    {
        FileOutcome.Corrupt or FileOutcome.Rotted or FileOutcome.Error or FileOutcome.Missing => "bad",
        FileOutcome.Outdated or FileOutcome.Placeholder => "fair",
        _ => "good",
    };

    private static string Join(IReadOnlyList<string> parts) => parts.Count switch
    {
        0 => string.Empty,
        1 => parts[0],
        2 => $"{parts[0]} and {parts[1]}",
        _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1],
    };
}
