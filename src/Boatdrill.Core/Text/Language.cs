using System.Globalization;

namespace Boatdrill.Core.Text;

/// <summary>
/// The language Boatdrill speaks: Korean when the UI culture is Korean, English otherwise. The app sets
/// the culture from its settings; boatdrill-cli runs with invariant globalization, so it always speaks
/// English, which keeps its output stable for scripts.
/// <para>
/// Each sentence is written whole in both languages, side by side, rather than assembled from translated
/// pieces: Korean orders a sentence differently, and its particles change with the word before them
/// (see <see cref="Korean"/>).
/// </para>
/// </summary>
public static class Language
{
    public const string EnglishCode = "en";
    public const string KoreanCode = "ko";

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>The language Windows displays, as the process found it before Boatdrill chose one.</summary>
    private static readonly CultureInfo WindowsUiCulture = CultureInfo.CurrentUICulture;

    public static bool IsKorean => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == KoreanCode;

    /// <summary>Whether Windows itself is displayed in Korean, which is what Boatdrill speaks unless told otherwise.</summary>
    public static bool WindowsIsKorean => WindowsUiCulture.TwoLetterISOLanguageName == KoreanCode;

    /// <summary>The English or the Korean, whichever Boatdrill speaks now.</summary>
    public static string Pick(string english, string korean) => IsKorean ? korean : english;

    /// <summary>
    /// Speaks <paramref name="code"/> ("en" or "ko") from now on, on every thread; null or empty for the
    /// language Windows is displayed in, when it is one of them.
    /// </summary>
    public static void Use(string? code)
    {
        var culture = code switch
        {
            KoreanCode => CultureInfo.GetCultureInfo("ko-KR"),
            EnglishCode => CultureInfo.GetCultureInfo("en-US"),
            _ => WindowsIsKorean ? CultureInfo.GetCultureInfo("ko-KR") : CultureInfo.GetCultureInfo("en-US"),
        };
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    /// <summary>"23 October 2026", or "2026년 10월 23일".</summary>
    public static string Date(DateTimeOffset when) =>
        when.ToLocalTime().ToString(IsKorean ? "yyyy년 M월 d일" : "d MMMM yyyy", Invariant);

    /// <summary>"23 October 2026, 14:05", or "2026년 10월 23일 14:05".</summary>
    public static string DateAndTime(DateTimeOffset when) =>
        when.ToLocalTime().ToString(IsKorean ? "yyyy년 M월 d일 HH:mm" : "d MMMM yyyy, HH:mm", Invariant);

    /// <summary>"Monday 27 October 2026 at 04:57", or "2026년 10월 27일 월요일 04:57".</summary>
    public static string DayDateAndTime(DateTimeOffset when) => IsKorean
        ? when.ToLocalTime().ToString("yyyy년 M월 d일 dddd HH:mm", CultureInfo.GetCultureInfo("ko-KR"))
        : when.ToLocalTime().ToString("dddd d MMMM yyyy 'at' HH:mm", Invariant);

    /// <summary>"A", "A and B", "A, B and C"; in Korean "A, B, C".</summary>
    public static string List(IReadOnlyList<string> items) => items.Count switch
    {
        0 => string.Empty,
        1 => items[0],
        _ when IsKorean => string.Join(", ", items),
        2 => $"{items[0]} and {items[1]}",
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };
}
