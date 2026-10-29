using Boatdrill.Core.Text;

namespace Boatdrill.Core.Drill;

/// <summary>One of the four parts of the score, as a tile: its name, its value and a line saying why.</summary>
public sealed record ScorePart(string Name, string Value, string Detail);

/// <summary>The four parts of a destination's score in words: the same on the dashboard, in a report and in the terminal.</summary>
public static class ScoreParts
{
    /// <summary>The tiles for a result, or none when nothing was read back.</summary>
    public static IReadOnlyList<ScorePart> Describe(DestinationResult r)
    {
        if (!r.Reachable || r.Sampled == 0) return [];

        var score = r.Score;
        return
        [
            new ScorePart("Coverage", Percent(score.Coverage),
                $"{r.Sampled - r.Missing - r.Placeholder - r.Errors:N0} of {r.Sampled:N0} found" +
                (score.CoverageMargin >= 0.005 ? $", ±{Percent(score.CoverageMargin)}" : string.Empty)),
            new ScorePart("Freshness", Percent(score.Freshness),
                r.Lag is { } lag && lag > TimeSpan.Zero ? Humanize.Age(lag) + " behind"
                : r.CanaryCopiedUtc is not null ? "up to date, by the canary"
                : "up to date"),
            new ScorePart("Integrity", Percent(score.Integrity), IntegrityDetail(r)),
            new ScorePart("Reachability", Percent(score.Reachability),
                score.Reachability >= 0.995 ? "there at every drill" : "missing at some drills"),
        ];
    }

    /// <summary>Whole percents, except that a share just short of all (or just above none) keeps a decimal, so 99.6% never reads as 100%.</summary>
    public static string Percent(double share)
    {
        var whole = Math.Round(share * 100);
        return (whole == 100 && share < 1) || (whole == 0 && share > 0)
            ? $"{Math.Round(share * 100, 1, MidpointRounding.ToZero):0.0}%"
            : $"{whole:0}%";
    }

    private static string IntegrityDetail(DestinationResult r) => (r.Corrupt, r.Rotted) switch
    {
        (> 0, > 0) => $"{r.Corrupt:N0} corrupt, {r.Rotted:N0} changed since verified",
        (1, _) => "1 corrupt copy",
        (> 1, _) => $"{r.Corrupt:N0} corrupt copies",
        (_, 1) => "1 copy changed since verified",
        (_, > 1) => $"{r.Rotted:N0} copies changed since verified",
        _ when r.Rechecked > 0 => $"{r.Rechecked:N0} read again, unchanged",
        _ => "no corrupt copies",
    };
}
