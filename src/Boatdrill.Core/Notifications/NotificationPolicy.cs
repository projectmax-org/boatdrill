using Boatdrill.Core.Drill;
using Boatdrill.Core.Scoring;
using Boatdrill.Core.Text;

namespace Boatdrill.Core.Notifications;

/// <summary>A notification's title and text. Windows shows 63 and 255 characters of them.</summary>
public sealed record Notice(string Title, string Text);

/// <summary>
/// What, if anything, a finished drill should say in a notification. Silence when every destination
/// would restore and every protected folder was found: a notification is for something the user needs to do.
/// </summary>
public static class NotificationPolicy
{
    public const int MaxText = 250;

    public static bool IsProblem(Verdict verdict) =>
        verdict is Verdict.NeedsAttention or Verdict.WouldNotRestore or Verdict.Unreachable or Verdict.CouldNotCheck;

    public static Notice? For(DrillReport report)
    {
        var problems = report.Destinations
            .Where(d => IsProblem(d.Score.Verdict))
            .OrderByDescending(d => d.Score.Verdict)
            .ToList();

        if (problems.Count == 0)
        {
            // Every backup would restore what was checked, but a folder that was not found was not checked at all.
            var missing = report.MissingSources;
            return missing.Count == 0
                ? null
                : new Notice(Verdicts.MissingHeadline(missing), Shorten(
                    Verdicts.MissingSources(missing) + Language.Pick(" Check the folder in Settings.", " 설정에서 폴더를 확인하세요."), MaxText));
        }

        if (problems.Count == 1)
        {
            var d = problems[0];
            return new Notice(Headline(d), Shorten(Details(d, report.FinishedUtc), MaxText));
        }

        var phrases = problems.Select(Headline).ToList();
        var text = Language.IsKorean
            ? string.Join(". ", phrases) + ". 자세한 내용은 Boatdrill에서 확인하세요."
            : Language.List(phrases) + ". Open Boatdrill for the details.";
        return new Notice(Language.Pick($"{problems.Count} backups need attention", $"확인이 필요한 백업이 {problems.Count}개 있습니다"), Shorten(text, MaxText));
    }

    /// <summary>
    /// A drill that could not finish at all, so that no backup's verdict came of it: said once, as the drill is tried
    /// again, since the backups' last verdicts are otherwise all the user sees.
    /// </summary>
    public static Notice ForFailedDrill(string reason) => new(
        Language.Pick("The drill could not finish", "복원 훈련을 끝내지 못했습니다"),
        Shorten(reason.Trim().TrimEnd('.') + Language.Pick(". Your backups were not checked this time.", ". 이번에는 백업을 확인하지 못했습니다."), MaxText));

    private static string Headline(DestinationResult d) => Verdicts.Phrase(d.DestinationName, d.Score.Verdict);

    /// <summary>What the title does not already say: the verdict's sentences after the first.</summary>
    private static string Details(DestinationResult d, DateTimeOffset finished)
    {
        if (d.Score.Verdict == Verdict.CouldNotCheck) return $"{d.Failure}. {Verdicts.Body(d, finished)}";
        if (d.Score.Verdict != Verdict.Unreachable) return Verdicts.Body(d, finished);

        if (Language.IsKorean)
        {
            var advice = d.Kind switch
            {
                DestinationKind.Restic or DestinationKind.Kopia => "설정에서 저장소를 확인하세요.",
                DestinationKind.Rclone => "설정에서 원격 저장소를 확인하세요.",
                _ => "드라이브를 연결하거나 공유 폴더를 확인하세요.",
            };
            return $"{d.ProbeDetail}. {advice} 다음 복원 훈련 때 다시 시도합니다.";
        }

        var fix = d.Kind switch
        {
            DestinationKind.Restic or DestinationKind.Kopia => "Check the repository in Settings",
            DestinationKind.Rclone => "Check the remote in Settings",
            _ => "Plug it in or check the share",
        };
        return $"{d.ProbeDetail}. {fix}, and Boatdrill will try again at the next drill.";
    }

    /// <summary>Cuts at the last sentence that fits, or at the last word with an ellipsis.</summary>
    internal static string Shorten(string text, int max)
    {
        if (text.Length <= max) return text;

        var cut = text.LastIndexOf(". ", max - 1, StringComparison.Ordinal);
        if (cut > max / 2) return text[..(cut + 1)];

        var space = text.LastIndexOf(' ', max - 2);
        return (space > 0 ? text[..space] : text[..(max - 1)]) + "…";
    }
}
