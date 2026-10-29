namespace Boatdrill.Core.Text;

/// <summary>
/// Korean particles that change with the sound before them: 은/는, 이/가, 을/를, 과/와, 으로/로. A word that
/// ends in a consonant takes the first form ("사진은"), one that ends in a vowel the second ("문서는"). The
/// names Boatdrill puts into sentences are the user's own, so they may end in Hangul, a number ("2027은",
/// read 이천이십칠) or Latin letters ("NAS는", read 나스); each is read the way a Korean speaker says it.
/// </summary>
public static class Korean
{
    private enum Ending
    {
        Vowel,
        Rieul,
        Consonant,
    }

    /// <summary>"문서는", "사진은": the topic particle.</summary>
    public static string Topic(string word) => word + (LastSound(word) == Ending.Vowel ? "는" : "은");

    /// <summary>"드라이브가", "사진이": the subject particle.</summary>
    public static string Subject(string word) => word + (LastSound(word) == Ending.Vowel ? "가" : "이");

    /// <summary>"드라이브를", "사진을": the object particle.</summary>
    public static string Object(string word) => word + (LastSound(word) == Ending.Vowel ? "를" : "을");

    /// <summary>"드라이브와", "사진과": "and" or "with".</summary>
    public static string With(string word) => word + (LastSound(word) == Ending.Vowel ? "와" : "과");

    /// <summary>"드라이브로", "파일로", "사진으로": "to", "by" or "as". ㄹ takes 로, like a vowel.</summary>
    public static string Toward(string word) => word + (LastSound(word) == Ending.Consonant ? "으로" : "로");

    /// <summary>"드라이브입니다", "사진입니다": "is", which is the same after a vowel or a consonant.</summary>
    public static string Is(string word) => word + "입니다";

    private static Ending LastSound(string word)
    {
        for (var i = word.Length - 1; i >= 0; i--)
        {
            var c = word[i];
            if (c is >= '가' and <= '힣')
            {
                var final = (c - '가') % 28;
                return final == 0 ? Ending.Vowel : final == 8 ? Ending.Rieul : Ending.Consonant;
            }

            if (char.IsAsciiDigit(c)) return NumberEnding(word, i);
            if (char.IsAsciiLetter(c)) return LatinEnding(word, i);

            // Another script: no way to tell, and a vowel is the likelier guess.
            if (char.IsLetterOrDigit(c)) return Ending.Vowel;

            // Brackets, quotes, spaces and the like are not read aloud: look at what comes before.
        }

        return Ending.Vowel;
    }

    /// <summary>A number as read in Sino-Korean: 1 일, 3 삼, 10 십, 100 백, 1,000 천, 10,000 만, 2 이.</summary>
    private static Ending NumberEnding(string word, int last)
    {
        var start = last;
        while (start > 0 && (char.IsAsciiDigit(word[start - 1]) || word[start - 1] == ',')) start--;
        var digits = word[start..(last + 1)].Replace(",", string.Empty);

        var zeros = digits.Length - digits.TrimEnd('0').Length;
        if (zeros == digits.Length) return Ending.Consonant; // 영

        return zeros switch
        {
            0 => digits[^1] switch
            {
                '1' or '7' or '8' => Ending.Rieul, // 일, 칠, 팔
                '3' or '6' => Ending.Consonant, // 삼, 육
                _ => Ending.Vowel, // 이, 사, 오, 구
            },
            1 or 2 or 3 => Ending.Consonant, // 십, 백, 천
            _ => (zeros / 4) switch
            {
                1 or 2 => Ending.Consonant, // 만, 억
                _ => Ending.Vowel, // 조
            },
        };
    }

    /// <summary>
    /// Latin letters: an abbreviation is read letter by letter ("SSD", 에스에스디; of the letters only L 엘,
    /// M 엠, N 엔 and R 알 end in a consonant), a word as it sounds, roughly ("Drive", 드라이브; "Team", 팀;
    /// "Laptop", 랩톱; "Google", 구글).
    /// </summary>
    private static Ending LatinEnding(string word, int last)
    {
        var start = last;
        while (start > 0 && char.IsAsciiLetter(word[start - 1])) start--;
        var letters = word[start..(last + 1)];

        if (letters.Length <= 4 && letters.All(char.IsAsciiLetterUpper))
        {
            return letters[^1] switch
            {
                'L' or 'R' => Ending.Rieul,
                'M' or 'N' => Ending.Consonant,
                _ => Ending.Vowel,
            };
        }

        var lower = letters.ToLowerInvariant();
        if (lower.EndsWith('l') || lower.EndsWith("le", StringComparison.Ordinal)) return Ending.Rieul;
        if (lower[^1] is 'm' or 'n' or 'p' || lower.EndsWith("ng", StringComparison.Ordinal)) return Ending.Consonant;

        // "Dock", 독, and "Book", 북, end in ㄱ; "Disk", 디스크, does not.
        if (lower[^1] == 'k' && lower.Length > 1 && lower[^2] is 'a' or 'e' or 'i' or 'o' or 'u' or 'c') return Ending.Consonant;

        return Ending.Vowel;
    }
}
