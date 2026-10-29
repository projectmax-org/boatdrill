namespace Boatdrill.Core.Text;

/// <summary>Numbers the way a person says them, in English or Korean. Everything the app shows goes through here.</summary>
public static class Humanize
{
    private static readonly string[] Units = ["bytes", "KB", "MB", "GB", "TB", "PB"];

    public static string Bytes(long bytes)
    {
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        if (unit > 0) return $"{value:0.#} {Units[unit]}";
        return Language.IsKorean ? $"{bytes:N0}바이트" : $"{bytes:N0} bytes";
    }

    /// <summary>"45 s", "12 min", "2 h 10 min", "3 days" (45초, 12분, 2시간 10분, 3일): how long something takes.</summary>
    public static string Duration(TimeSpan span)
    {
        var korean = Language.IsKorean;
        if (span.TotalSeconds < 60)
        {
            var seconds = Math.Max(1, (int)Math.Round(span.TotalSeconds));
            return korean ? $"{seconds}초" : $"{seconds} s";
        }

        if (span.TotalMinutes < 60)
        {
            var minutes = (int)Math.Round(span.TotalMinutes);
            return korean ? $"{minutes}분" : $"{minutes} min";
        }

        if (span.TotalHours < 48)
        {
            var hours = (int)span.TotalHours;
            if (span.Minutes == 0) return korean ? $"{hours}시간" : $"{hours} h";
            return korean ? $"{hours}시간 {span.Minutes}분" : $"{hours} h {span.Minutes} min";
        }

        var days = (int)Math.Round(span.TotalDays);
        return korean ? $"{days}일" : $"{days} days";
    }

    /// <summary>"3 minutes", "1 hour", "9 days", "2 months" (3분, 1시간, 9일, 2개월): how long ago something was.</summary>
    public static string Age(TimeSpan span)
    {
        if (span.TotalMinutes < 1) return Language.Pick("less than a minute", "1분 미만");
        if (span.TotalHours < 1) return Unit((int)Math.Round(span.TotalMinutes), "minute", "분");
        if (span.TotalDays < 1) return Unit((int)Math.Round(span.TotalHours), "hour", "시간");
        if (span.TotalDays < 60) return Unit((int)Math.Round(span.TotalDays), "day", "일");
        if (span.TotalDays < 730) return Unit((int)Math.Round(span.TotalDays / 30.4), "month", "개월");
        return Unit((int)Math.Round(span.TotalDays / 365.25), "year", "년");
    }

    /// <summary>"in less than a minute", "in 5 minutes", "in 3 days" (곧, 5분 후, 3일 후): how long until something happens.</summary>
    public static string Until(TimeSpan span)
    {
        if (span <= TimeSpan.FromMinutes(1)) return Language.Pick("in less than a minute", "곧");
        return Language.IsKorean ? Age(span) + " 후" : "in " + Age(span);
    }

    /// <summary>"just now", "5 minutes ago", "3 days ago" (방금, 5분 전, 3일 전).</summary>
    public static string Ago(TimeSpan span)
    {
        if (span < TimeSpan.FromMinutes(1)) return Language.Pick("just now", "방금");
        return Language.IsKorean ? Age(span) + " 전" : Age(span) + " ago";
    }

    /// <summary>"3 files", or "파일 3개": a count of things, in English with a plural, in Korean with a counter.</summary>
    public static string Count(long count, string englishNoun, string koreanNoun) =>
        Language.IsKorean ? $"{koreanNoun} {count:N0}개" : Plural(count, englishNoun);

    public static string Plural(long count, string noun) => count == 1 ? $"1 {noun}" : $"{count:N0} {noun}s";

    /// <summary>"5 minutes", or "5분": a number of a unit of time.</summary>
    private static string Unit(int count, string english, string korean) => Language.IsKorean ? $"{count:N0}{korean}" : Plural(count, english);
}
