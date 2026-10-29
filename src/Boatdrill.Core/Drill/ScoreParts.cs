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
        var found = r.Sampled - r.Missing - r.Placeholder - r.Errors;
        var margin = score.CoverageMargin >= 0.005 ? $", ±{Percent(score.CoverageMargin)}" : string.Empty;
        var behind = r.Lag is { } lag && lag > TimeSpan.Zero ? Humanize.Age(lag) : null;
        var everyDrill = score.Reachability >= 0.995;

        if (Language.IsKorean)
        {
            return
            [
                new ScorePart("포함률", Percent(score.Coverage), $"{r.Sampled:N0}개 중 {found:N0}개 있음{margin}"),
                new ScorePart("최신성", Percent(score.Freshness),
                    behind is not null ? behind + " 뒤처짐" : r.CanaryCopiedUtc is not null ? "최신 상태 (카나리아 기준)" : "최신 상태"),
                new ScorePart("무결성", Percent(score.Integrity), IntegrityDetail(r)),
                new ScorePart("연결률", Percent(score.Reachability), everyDrill ? "훈련 때마다 연결됨" : "연결되지 않은 훈련이 있음"),
            ];
        }

        return
        [
            new ScorePart("Coverage", Percent(score.Coverage), $"{found:N0} of {r.Sampled:N0} found{margin}"),
            new ScorePart("Freshness", Percent(score.Freshness),
                behind is not null ? behind + " behind" : r.CanaryCopiedUtc is not null ? "up to date, by the canary" : "up to date"),
            new ScorePart("Integrity", Percent(score.Integrity), IntegrityDetail(r)),
            new ScorePart("Reachability", Percent(score.Reachability), everyDrill ? "there at every drill" : "missing at some drills"),
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

    private static string IntegrityDetail(DestinationResult r) => Language.IsKorean
        ? (r.Corrupt, r.Rotted) switch
        {
            (> 0, > 0) => $"손상 {r.Corrupt:N0}개, 확인 뒤 바뀐 사본 {r.Rotted:N0}개",
            (> 0, _) => $"손상된 사본 {r.Corrupt:N0}개",
            (_, > 0) => $"확인 뒤 바뀐 사본 {r.Rotted:N0}개",
            _ when r.Rechecked > 0 => $"{r.Rechecked:N0}개를 다시 읽음, 변화 없음",
            _ => "손상된 사본 없음",
        }
        : (r.Corrupt, r.Rotted) switch
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
