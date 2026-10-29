using System.Globalization;
using System.Text;

namespace Boatdrill.Core.Drill;

/// <summary>
/// The canary: a small file Boatdrill keeps in each source folder when asked to, with a stamp added after
/// every drill. A backup that runs copies the newest stamp, so the stamps missing from a destination's copy
/// say how long that backup has been behind, even when nothing else in the folder has changed.
/// </summary>
public static class Canary
{
    public const string FileName = "Boatdrill canary.txt";

    /// <summary>Stamps kept in the file, newest first.</summary>
    public const int KeepStamps = 12;

    /// <summary>A backup that runs once a day may not yet have copied a stamp written less than a day ago.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromDays(1);

    private const string StampPrefix = "stamp: ";

    private const string Header = """
        Boatdrill writes this file to measure how far behind your backups are.

        After every drill it adds the time below. A backup that runs copies this file along with the rest,
        so the newest time in a backup's copy shows when that backup last ran. Nothing else uses this file.
        To stop it, turn off canary files in Boatdrill's settings; you can then delete it.


        """;

    public static string PathIn(string sourceRoot) => Path.Combine(sourceRoot, FileName);

    /// <summary>The stamps in a canary file's text, newest first. Other lines are ignored.</summary>
    public static IReadOnlyList<DateTimeOffset> Parse(string text) =>
        text.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith(StampPrefix, StringComparison.Ordinal))
            .Select(line => DateTimeOffset.TryParse(line[StampPrefix.Length..], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when)
                ? when.ToUniversalTime()
                : (DateTimeOffset?)null)
            .OfType<DateTimeOffset>()
            .OrderByDescending(t => t)
            .ToList();

    /// <summary>The stamps in a source's canary file; none when there is no file or it cannot be read.</summary>
    public static IReadOnlyList<DateTimeOffset> Read(string sourceRoot)
    {
        try
        {
            var path = PathIn(sourceRoot);
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Adds a stamp to a source's canary file, making the file if need be. False when it cannot be written.</summary>
    public static bool Stamp(string sourceRoot, DateTimeOffset now)
    {
        if (!Directory.Exists(sourceRoot)) return false;

        // Whole seconds, so the stamp read back from any copy compares equal to the one written.
        var stamp = new DateTimeOffset(now.UtcTicks - now.UtcTicks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
        var text = new StringBuilder(Header.ReplaceLineEndings());
        foreach (var kept in Read(sourceRoot).Append(stamp).Distinct().OrderByDescending(t => t).Take(KeepStamps))
        {
            text.Append(StampPrefix).Append(kept.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)).AppendLine();
        }

        try
        {
            File.WriteAllText(PathIn(sourceRoot), text.ToString());
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// When a destination started falling behind, by the canary: the oldest stamp written at the source that its
    /// copy lacks, once it is older than <see cref="Grace"/>. Null when the copy is up to date or the source has no stamps.
    /// </summary>
    public static DateTimeOffset? BehindSince(IReadOnlyList<DateTimeOffset> sourceStamps, IReadOnlyList<DateTimeOffset> copyStamps, DateTimeOffset now)
    {
        DateTimeOffset? copied = copyStamps.Count > 0 ? copyStamps.Max() : null;
        return sourceStamps
            .Where(s => (copied is null || s > copied) && now - s >= Grace)
            .Select(s => (DateTimeOffset?)s)
            .Min();
    }
}
