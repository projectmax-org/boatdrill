using System.Globalization;
using System.Text.RegularExpressions;

namespace Boatdrill.Core.Tools;

/// <summary>Times as Go programs (restic, rclone) print them: RFC 3339 with up to nine digits of seconds; .NET keeps seven.</summary>
internal static partial class GoTime
{
    public static DateTimeOffset? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;

        var trimmed = LongFraction().Replace(text, m => m.Groups[1].Value);
        return DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when)
            ? when.ToUniversalTime()
            : null;
    }

    [GeneratedRegex(@"(\.\d{7})\d+")]
    private static partial Regex LongFraction();
}
