using Boatdrill.Core.Drill;
using Boatdrill.Core.Scoring;
using Boatdrill.Core.Verification;

namespace Boatdrill.UI;

/// <summary>
/// The words and the colour family for a verdict, from Core so the app, the report and the terminal agree.
/// Views turn the kind into a brush with a DataTrigger, so the colour follows the light and dark palettes.
/// </summary>
public static class VerdictStyle
{
    public const string Good = "good";
    public const string Fair = "fair";
    public const string Bad = "bad";
    public const string None = "none";

    public static string Kind(Verdict verdict) => Verdicts.Tone(verdict);

    public static string Text(Verdict verdict) => Verdicts.Words(verdict);

    public static string Kind(FileOutcome outcome) => Verdicts.Tone(outcome);

    public static string Text(FileOutcome outcome) => Verdicts.Words(outcome);
}
