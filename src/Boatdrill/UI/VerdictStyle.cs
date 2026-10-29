using Boatdrill.Core.Scoring;
using Boatdrill.Core.Verification;

namespace Boatdrill.UI;

/// <summary>
/// The words and the colour family for a verdict. Views turn the kind into a brush with a DataTrigger, so
/// the colour follows the light and dark palettes.
/// </summary>
public static class VerdictStyle
{
    public const string Good = "good";
    public const string Fair = "fair";
    public const string Bad = "bad";
    public const string None = "none";

    public static string Kind(Verdict verdict) => verdict switch
    {
        Verdict.WouldRestore => Good,
        Verdict.WouldMostlyRestore => Fair,
        Verdict.NeedsAttention or Verdict.WouldNotRestore or Verdict.Unreachable => Bad,
        _ => None,
    };

    public static string Text(Verdict verdict) => verdict switch
    {
        Verdict.WouldRestore => "would restore",
        Verdict.WouldMostlyRestore => "would mostly restore",
        Verdict.NeedsAttention => "needs attention",
        Verdict.WouldNotRestore => "would not restore",
        Verdict.Unreachable => "could not be reached",
        _ => "nothing to check",
    };

    public static string Kind(FileOutcome outcome) => outcome switch
    {
        FileOutcome.Corrupt or FileOutcome.Error or FileOutcome.Missing => Bad,
        FileOutcome.Outdated or FileOutcome.Placeholder => Fair,
        _ => Good,
    };

    public static string Text(FileOutcome outcome) => outcome switch
    {
        FileOutcome.Corrupt => "Corrupt",
        FileOutcome.Error => "Unreadable",
        FileOutcome.Missing => "Missing",
        FileOutcome.Outdated => "Behind",
        FileOutcome.Placeholder => "Cloud only",
        _ => "Verified",
    };
}
