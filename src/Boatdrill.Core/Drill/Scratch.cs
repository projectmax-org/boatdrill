namespace Boatdrill.Core.Drill;

/// <summary>The scratch folder, where read-back lands. Each drill works in its own folder and empties it.</summary>
internal static class Scratch
{
    public const string Prefix = "boatdrill-";

    /// <summary>A drill folder left behind this long ago belongs to no running drill.</summary>
    private static readonly TimeSpan Abandoned = TimeSpan.FromHours(12);

    /// <summary>Between attempts at a drill's own folder: a virus scanner may still be looking at a file just written.</summary>
    private static readonly TimeSpan[] Patient = [TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(750), TimeSpan.FromSeconds(1)];

    /// <summary>A folder left hours ago has nothing looking at it any more: one more attempt, at once.</summary>
    private static readonly TimeSpan[] Brief = [TimeSpan.Zero];

    /// <summary>Every entry, hidden ones too, but never through a junction or link: what that points at is not scratch.</summary>
    private static readonly EnumerationOptions EverythingButLinks = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    /// <summary>Deletes what earlier drills could not, such as a file another program held open at the time.</summary>
    public static void Sweep(string parent, DateTimeOffset now)
    {
        try
        {
            foreach (var folder in new DirectoryInfo(parent).EnumerateDirectories(Prefix + "*"))
            {
                // Quickly: a folder that cannot be deleted at all would otherwise cost every later drill the waits.
                if (now.UtcDateTime - folder.CreationTimeUtc > Abandoned) TryDelete(folder.FullName, Brief);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing to sweep, or nothing that can be swept now; the next drill tries again.
        }
    }

    /// <summary>
    /// Deletes a folder tree, clearing the read-only flag a restore may have copied onto it, and waiting a moment for
    /// a virus scanner that is still looking at a file just written.
    /// </summary>
    public static bool TryDelete(string directory) => TryDelete(directory, Patient);

    private static bool TryDelete(string directory, TimeSpan[] waits)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!Directory.Exists(directory)) return true;
                if (attempt > 0) ClearAttributes(directory);
                Directory.Delete(directory, recursive: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == waits.Length) return false;
                Thread.Sleep(waits[attempt]);
            }
        }
    }

    /// <summary>
    /// Clears the read-only, hidden and system flags on everything in a folder tree, folders included, as far as it
    /// can: a restore can bring back Windows' attributes, among them the read-only flag of a folder such as
    /// C:\Users, and a read-only folder cannot be deleted. A junction or link is left as it is, and so is what it
    /// points at; deleting the tree removes the link alone.
    /// </summary>
    private static void ClearAttributes(string directory)
    {
        var root = new DirectoryInfo(directory);
        foreach (var entry in root.EnumerateFileSystemInfos("*", EverythingButLinks).Prepend(root))
        {
            try
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) == 0) entry.Attributes = FileAttributes.Normal;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left for the delete to report.
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
