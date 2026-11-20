using System.Text;
using Boatdrill.Core.Notifications;
using Boatdrill.Core.Scoring;
using Boatdrill.Core.Text;
using Boatdrill.Core.Verification;

namespace Boatdrill.Core.Drill;

/// <summary>
/// Turns a result into the plain sentences the app shows under the score, in English or Korean. They are
/// built from the result's numbers alone, so a result stored in English reads in Korean too.
/// </summary>
public static class Verdicts
{
    public static string Sentence(DestinationResult r, DateTimeOffset now)
    {
        var body = Body(r, now);
        return body.Length == 0 ? Lead(r) : Lead(r) + " " + body;
    }

    /// <summary>The verdict, as the first sentence: "Backup drive would restore."</summary>
    public static string Lead(DestinationResult r)
    {
        var name = r.DestinationName;
        if (Language.IsKorean)
        {
            return r.Score.Verdict switch
            {
                Verdict.Unreachable => $"{name}에 연결할 수 없습니다: {r.ProbeDetail}.",
                Verdict.CouldNotCheck => $"{Korean.Object(name)} 확인하지 못했습니다: {r.Failure}.",
                Verdict.NothingToCheck => $"{name}에는 연결되지만 표본으로 고른 파일이 없어 아직 확인할 것이 없습니다.",
                Verdict.WouldRestore => $"{name}에서 복원할 수 있습니다.",
                Verdict.WouldMostlyRestore => $"{name}에서 대부분 복원할 수 있습니다.",
                Verdict.NeedsAttention => $"{name}에 확인할 문제가 있습니다.",
                _ => $"{name}에서는 복원할 수 없습니다.",
            };
        }

        return r.Score.Verdict switch
        {
            Verdict.Unreachable => $"{name} could not be reached: {r.ProbeDetail}.",
            Verdict.CouldNotCheck => $"{name} could not be checked: {r.Failure}.",
            Verdict.NothingToCheck => $"{name} is reachable, but no files were sampled, so there is nothing to verify yet.",
            Verdict.WouldRestore => $"{name} would restore.",
            Verdict.WouldMostlyRestore => $"{name} would mostly restore.",
            Verdict.NeedsAttention => $"{name} needs attention.",
            _ => $"{name} would not restore.",
        };
    }

    /// <summary>Everything after the lead: what was read back, what is missing or older, how long a restore would take.</summary>
    public static string Body(DestinationResult r, DateTimeOffset now)
    {
        var korean = Language.IsKorean;
        switch (r.Score.Verdict)
        {
            case Verdict.Unreachable when korean:
                return r.Kind switch
                {
                    DestinationKind.Restic or DestinationKind.Kopia => "설정에서 저장소를 확인한 다음 복원 훈련을 다시 실행하세요.",
                    DestinationKind.Rclone => "설정에서 원격 저장소를 확인한 다음 복원 훈련을 다시 실행하세요.",
                    _ => "드라이브를 연결하거나 공유 폴더를 확인한 다음 복원 훈련을 다시 실행하세요.",
                };
            case Verdict.Unreachable:
                return r.Kind switch
                {
                    DestinationKind.Restic or DestinationKind.Kopia => "Check the repository in Settings, then run a drill.",
                    DestinationKind.Rclone => "Check the remote in Settings, then run a drill.",
                    _ => "Plug it in or check the share, then run a drill.",
                };
            case Verdict.CouldNotCheck:
                return Language.Pick(
                    "Whether it would restore is not known; the next drill will check it again.",
                    "복원할 수 있는지는 아직 알 수 없습니다. 다음 복원 훈련 때 다시 확인합니다.");
            case Verdict.NothingToCheck:
                return string.Empty;
        }

        var text = new StringBuilder(korean ? KoreanFacts(r) : EnglishFacts(r));

        var lost = r.Bindings.Where(b => b.ProbeTried > 0 && b.ProbeHits == 0).ToList();
        var unexplained = lost.Where(b => b.Problem is null).Select(b => b.SourceName).ToList();
        if (unexplained.Count > 0)
        {
            text.Append(korean
                ? $" {Language.List(unexplained)}의 사본을 {r.RootPath}에서 찾지 못했습니다."
                : $" No copy of {Language.List(unexplained)} was found under {r.RootPath}.");
        }

        foreach (var explained in lost.Where(b => b.Problem is not null))
        {
            text.Append($" {explained.SourceName}: {explained.Problem}.");
        }

        foreach (var warned in r.Bindings.Where(b => b.Warning is not null && !lost.Contains(b)))
        {
            text.Append($" {warned.SourceName}: {warned.Warning}.");
        }

        if (r.BehindSince is { } since)
        {
            text.Append(korean
                ? $" 아직 여기에 복사되지 않은 가장 오래된 변경 사항은 {Language.Date(since)}({Humanize.Ago(now - since)})에 생겼습니다."
                : $" The oldest change not yet copied here is from {Language.Date(since)} ({Humanize.Age(now - since)} ago).");
        }

        if (r.CanaryBehindSince is not null)
        {
            text.Append((r.CanaryCopiedUtc, korean) switch
            {
                ({ } copied, true) => $" 카나리아 기록은 {Language.DateAndTime(copied)}에 남긴 것까지만 여기에 있고, 그 뒤의 기록은 아직 복사되지 않았습니다.",
                ({ } copied, false) => $" The newest canary stamp copied here is from {Language.DateAndTime(copied)}; the later ones have not been copied.",
                (null, true) => " 여기에서 카나리아 파일의 사본을 찾지 못했습니다.",
                (null, false) => " No copy of the canary file was found here.",
            });
        }

        if (r.EstimatedFullRestore is { } estimate && r.PopulationBytes > 0)
        {
            text.Append(korean
                ? $" 측정한 속도라면 {Humanize.Bytes(r.PopulationBytes)} 전체를 복원하는 데 약 {Korean.Subject(Humanize.Duration(estimate))} 걸립니다."
                : $" A full restore of {Humanize.Bytes(r.PopulationBytes)} would take about {Humanize.Duration(estimate)} at the speed measured.");
        }

        return text.ToString();
    }

    private static string EnglishFacts(DestinationResult r)
    {
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

        var joined = Language.List(facts);
        return char.ToUpperInvariant(joined[0]) + joined[1..] + ".";
    }

    /// <summary>The same facts as sentences of their own, which is how Korean says a list of them.</summary>
    private static string KoreanFacts(DestinationResult r)
    {
        // None and one are said apart: "0개가 일치했습니다" would read as good news, and "1개가 모두" is not said.
        var matched = (r.Verified, r.Sampled) switch
        {
            (1, 1) => "표본 파일 1개가 원본과 일치했습니다.",
            (0, 1) => "표본 파일 1개가 원본과 일치하지 않았습니다.",
            (0, var sampled) => $"표본 파일 {sampled:N0}개 중 원본과 일치한 파일이 없습니다.",
            var (verified, sampled) when verified == sampled => $"표본 파일 {sampled:N0}개가 모두 원본과 일치했습니다.",
            var (verified, sampled) => $"표본 파일 {sampled:N0}개 중 {verified:N0}개가 원본과 일치했습니다.",
        };
        var facts = new List<string> { matched };
        if (r.Missing > 0) facts.Add($"{r.Missing:N0}개는 사본이 없습니다.");
        if (r.Outdated > 0) facts.Add($"{r.Outdated:N0}개는 원본보다 오래된 사본입니다.");
        if (r.Corrupt > 0) facts.Add($"{r.Corrupt:N0}개는 원본보다 오래되지 않았는데도 내용이 달라 손상이 의심됩니다.");
        if (r.Placeholder > 0) facts.Add($"{r.Placeholder:N0}개는 클라우드에만 있고 이 디스크에는 없습니다.");
        if (r.Errors > 0) facts.Add($"{r.Errors:N0}개는 읽을 수 없었습니다.");
        if (r.Rotted > 0)
        {
            facts.Add($"이전 훈련에서 확인한 사본 {r.Rechecked:N0}개 중 {r.Rotted:N0}개가 크기와 날짜는 그대로인데 내용이 바뀌어 비트 부패가 의심됩니다.");
        }
        else if (r.Rechecked > 0)
        {
            facts.Add($"이전 훈련에서 확인한 사본 {r.Rechecked:N0}개는 그대로입니다.");
        }

        return string.Join(" ", facts);
    }

    /// <summary>The answer first, for a set of results: how the backups stand, in a few words.</summary>
    public static string Headline(IReadOnlyList<DestinationResult> results)
    {
        if (results.Count == 0) return Language.Pick("No backups were checked", "확인한 백업이 없습니다");

        var problems = results.Where(r => NotificationPolicy.IsProblem(r.Score.Verdict)).ToList();
        var korean = Language.IsKorean;

        // Not one file was read back: that says nothing about whether the backups would restore.
        if (problems.Count == 0 && results.Any(r => r.Score.Verdict == Verdict.NothingToCheck))
        {
            return Language.Pick("Nothing could be checked", "확인할 수 있는 파일이 없었습니다");
        }

        return problems.Count switch
        {
            0 when results.All(r => r.Score.Verdict != Verdict.WouldMostlyRestore) => korean
                ? results.Count == 1 ? "백업에서 복원할 수 있습니다" : "모든 백업에서 복원할 수 있습니다"
                : results.Count == 1 ? "Your backup would restore" : "Your backups would restore",
            0 => korean
                ? "백업에서 대부분 복원할 수 있습니다"
                : results.Count == 1 ? "Your backup would mostly restore" : "Your backups would mostly restore",
            1 => Phrase(problems[0].DestinationName, problems[0].Score.Verdict),
            _ => korean ? $"확인이 필요한 백업이 {problems.Count}개 있습니다" : $"{problems.Count} backups need attention",
        };
    }

    /// <summary>
    /// The protected folders a drill did not find, as a sentence; empty when it found them all. One folder comes
    /// with its path, since that is where the user has to look.
    /// </summary>
    public static string MissingSources(IReadOnlyList<SourceSummary> missing)
    {
        if (missing.Count == 0) return string.Empty;

        var names = Language.List(missing.Select(s => s.Name).ToList());
        if (Language.IsKorean)
        {
            // "사진 폴더(C:\Users\you\Pictures)를": the particle goes with the name, not with the path in brackets.
            var folders = missing.Count == 1 ? $"{Korean.Folder(names)}({missing[0].RootPath})" : Korean.Folder(names);
            return $"{Korean.Object(folders)} 찾을 수 없어 확인하지 못했습니다.";
        }

        return missing.Count == 1
            ? $"{names} was not found at {missing[0].RootPath}, so none of it was checked."
            : $"{names} were not found, so none of their files were checked.";
    }

    /// <summary>
    /// Why a drill read nothing back, from what its sources held: "No file could be read: 1,204 are online only and 3
    /// are too large." Empty when files were sampled.
    /// </summary>
    public static string NothingChecked(IReadOnlyList<SourceSummary> sources)
    {
        if (sources.Sum(s => s.Sampled) > 0) return string.Empty;

        long online = sources.Sum(s => s.SkippedOnlineOnlyFiles), large = sources.Sum(s => s.SkippedLargeFiles), fresh = sources.Sum(s => s.SkippedChurningFiles);
        var reasons = new List<string>();
        if (Language.IsKorean)
        {
            if (online > 0) reasons.Add($"온라인 전용 파일 {online:N0}개");
            if (large > 0) reasons.Add($"너무 큰 파일 {large:N0}개");
            if (fresh > 0) reasons.Add($"최근 몇 시간 사이에 바뀐 파일 {fresh:N0}개");
            return reasons.Count == 0
                ? "보호하는 폴더에 확인할 파일이 없습니다."
                : $"읽을 수 있는 파일이 없었습니다. 제외한 파일: {string.Join(", ", reasons)}.";
        }

        if (online > 0) reasons.Add($"{online:N0} {(online == 1 ? "is" : "are")} online only");
        if (large > 0) reasons.Add($"{large:N0} {(large == 1 ? "is" : "are")} too large");
        if (fresh > 0) reasons.Add($"{fresh:N0} changed in the last few hours");
        return reasons.Count == 0
            ? "The protected folders hold no files to check."
            : $"No file could be read: {Language.List(reasons)}.";
    }

    /// <summary>The same as a headline, for a drill whose backups are otherwise fine: "Pictures was not found".</summary>
    public static string MissingHeadline(IReadOnlyList<SourceSummary> missing) => (missing.Count, Language.IsKorean) switch
    {
        (1, true) => $"{Korean.Object(Korean.Folder(missing[0].Name))} 찾을 수 없습니다",
        (1, false) => $"{missing[0].Name} was not found",
        (_, true) => $"보호하는 폴더 {missing.Count}개를 찾을 수 없습니다",
        (_, false) => $"{missing.Count} protected folders were not found",
    };

    /// <summary>A backup and its verdict, as a headline: "Family NAS needs attention".</summary>
    public static string Phrase(string name, Verdict verdict)
    {
        if (!Language.IsKorean) return $"{name} {Words(verdict)}";

        return verdict switch
        {
            Verdict.WouldRestore => $"{name}에서 복원할 수 있습니다",
            Verdict.WouldMostlyRestore => $"{name}에서 대부분 복원할 수 있습니다",
            Verdict.NeedsAttention => $"{name}에 확인할 문제가 있습니다",
            Verdict.WouldNotRestore => $"{name}에서는 복원할 수 없습니다",
            Verdict.Unreachable => $"{name}에 연결할 수 없습니다",
            Verdict.CouldNotCheck => $"{Korean.Object(name)} 확인하지 못했습니다",
            _ => $"{name}에서 확인할 것이 없습니다",
        };
    }

    /// <summary>A verdict in words: in English as it follows a destination's name, in Korean as a label.</summary>
    public static string Words(Verdict verdict) => Language.IsKorean
        ? verdict switch
        {
            Verdict.WouldRestore => "복원 가능",
            Verdict.WouldMostlyRestore => "대부분 복원 가능",
            Verdict.NeedsAttention => "확인 필요",
            Verdict.WouldNotRestore => "복원 불가",
            Verdict.Unreachable => "연결 안 됨",
            Verdict.CouldNotCheck => "확인 못 함",
            _ => "확인할 것 없음",
        }
        : verdict switch
        {
            Verdict.WouldRestore => "would restore",
            Verdict.WouldMostlyRestore => "would mostly restore",
            Verdict.NeedsAttention => "needs attention",
            Verdict.WouldNotRestore => "would not restore",
            Verdict.Unreachable => "could not be reached",
            Verdict.CouldNotCheck => "could not be checked",
            _ => "nothing to check",
        };

    /// <summary>A file's outcome as a short label.</summary>
    public static string Words(FileOutcome outcome) => Language.IsKorean
        ? outcome switch
        {
            FileOutcome.Corrupt => "손상됨",
            FileOutcome.Rotted => "변질됨",
            FileOutcome.Error => "읽을 수 없음",
            FileOutcome.Missing => "사본 없음",
            FileOutcome.Outdated => "이전 버전",
            FileOutcome.Placeholder => "클라우드에만",
            _ => "확인됨",
        }
        : outcome switch
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
        Verdict.NeedsAttention or Verdict.WouldNotRestore or Verdict.Unreachable or Verdict.CouldNotCheck => "bad",
        _ => "none",
    };

    public static string Tone(FileOutcome outcome) => outcome switch
    {
        FileOutcome.Corrupt or FileOutcome.Rotted or FileOutcome.Error or FileOutcome.Missing => "bad",
        FileOutcome.Outdated or FileOutcome.Placeholder => "fair",
        _ => "good",
    };
}
