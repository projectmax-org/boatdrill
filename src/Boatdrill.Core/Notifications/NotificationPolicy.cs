using Boatdrill.Core.Drill;
using Boatdrill.Core.Scoring;

namespace Boatdrill.Core.Notifications;

/// <summary>A notification's title and text. Windows shows 63 and 255 characters of them.</summary>
public sealed record Notice(string Title, string Text);

/// <summary>
/// What, if anything, a finished drill should say in a notification. Silence when every destination
/// would restore: a notification is for something the user needs to do.
/// </summary>
public static class NotificationPolicy
{
    public const int MaxText = 250;

    public static bool IsProblem(Verdict verdict) =>
        verdict is Verdict.NeedsAttention or Verdict.WouldNotRestore or Verdict.Unreachable;

    public static Notice? For(DrillReport report)
    {
        var problems = report.Destinations
            .Where(d => IsProblem(d.Score.Verdict))
            .OrderByDescending(d => d.Score.Verdict)
            .ToList();

        if (problems.Count == 0) return null;

        if (problems.Count == 1)
        {
            var d = problems[0];
            return new Notice(Headline(d), Shorten(Details(d), MaxText));
        }

        var phrases = problems.Select(Headline).ToList();
        var text = string.Join(", ", phrases.Take(phrases.Count - 1)) + " and " + phrases[^1] + ". Open Boatdrill for the details.";
        return new Notice($"{problems.Count} backups need attention", Shorten(text, MaxText));
    }

    private static string Headline(DestinationResult d) => d.Score.Verdict switch
    {
        Verdict.Unreachable => $"{d.DestinationName} could not be reached",
        Verdict.WouldNotRestore => $"{d.DestinationName} would not restore",
        _ => $"{d.DestinationName} needs attention",
    };

    /// <summary>The verdict sentence without its first sentence, which the title already says.</summary>
    private static string Details(DestinationResult d)
    {
        if (d.Score.Verdict == Verdict.Unreachable)
        {
            return $"{d.ProbeDetail}. Plug it in or check the share, and Boatdrill will try again at the next drill.";
        }

        var sentence = d.Sentence;
        var end = sentence.IndexOf(". ", StringComparison.Ordinal);
        return end > 0 && sentence.StartsWith(d.DestinationName, StringComparison.Ordinal) ? sentence[(end + 2)..] : sentence;
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
