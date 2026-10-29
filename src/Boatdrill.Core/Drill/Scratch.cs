namespace Boatdrill.Core.Drill;

/// <summary>The scratch folder, where read-back lands. Each drill works in its own folder and empties it.</summary>
internal static class Scratch
{
    public const string Prefix = "boatdrill-";

    /// <summary>A drill folder left behind this long ago belongs to no running drill.</summary>
    private static readonly TimeSpan Abandoned = TimeSpan.FromHours(12);

    /// <summary>Deletes what earlier drills could not, such as a file another program held open at the time.</summary>
    public static void Sweep(string parent, DateTimeOffset now)
    {
        try
        {
            foreach (var folder in new DirectoryInfo(parent).EnumerateDirectories(Prefix + "*"))
            {
                if (now.UtcDateTime - folder.CreationTimeUtc > Abandoned) TryDelete(folder.FullName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing to sweep, or nothing that can be swept now; the next drill tries again.
        }
    }

    /// <summary>
    /// Deletes a folder tree, clearing the read-only flag a restore may have copied onto a file, and waiting a
    /// moment for a virus scanner that is still looking at a file just written.
    /// </summary>
    public static bool TryDelete(string directory)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (!Directory.Exists(directory)) return true;
                if (attempt > 1)
                {
                    foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(directory, recursive: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 3) return false;
                Thread.Sleep(TimeSpan.FromMilliseconds(250 * attempt));
            }
        }
    }

    public static void TryDeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Swept with the drill's folder.
        }
    }
}
