namespace Boatdrill.Core.Drill;

/// <summary>
/// The scratch folder, where read-back lands. Each drill, rehearsal and scan works in a folder of its own, made with
/// <see cref="Create"/>, and empties it.
/// </summary>
internal static class Scratch
{
    public const string Prefix = "boatdrill-";

    /// <summary>The file in each folder that its owner keeps open, shared with no one, while it works there. No adapter writes one by this name.</summary>
    public const string LockFile = "boatdrill.lock";

    /// <summary>
    /// Only a folder made this long ago is swept, and only once its lock can be had: a folder made a moment ago may not
    /// have its lock file yet.
    /// </summary>
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

    /// <summary>
    /// Makes a folder of one's own in the scratch directory and holds its lock until the folder is disposed, so that no
    /// sweep, in this process or another, takes it for one left behind however long the work goes on.
    /// </summary>
    public static ScratchFolder Create(string parent, string name)
    {
        var folder = Directory.CreateDirectory(Path.Combine(parent, name)).FullName;
        return new ScratchFolder(folder, new FileStream(Path.Combine(folder, LockFile), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
    }

    /// <summary>
    /// Deletes what earlier drills, rehearsals and scans left behind, such as a file another program held open at the
    /// time, but never a folder that one of them, here or in another Boatdrill, still works in.
    /// </summary>
    public static void Sweep(string parent, DateTimeOffset now)
    {
        try
        {
            foreach (var folder in new DirectoryInfo(parent).EnumerateDirectories(Prefix + "*"))
            {
                // Quickly: a folder that cannot be deleted at all would otherwise cost every later drill the waits.
                if (now.UtcDateTime - folder.CreationTimeUtc > Abandoned && !InUse(folder.FullName)) TryDelete(folder.FullName, Brief);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing to sweep, or nothing that can be swept now; the next drill tries again.
        }
    }

    /// <summary>
    /// Whether the folder's owner still holds its lock file open. The sweep asks for the lock rather than counting on the
    /// delete to fail: on Windows a delete fails at the held file only after emptying the rest of the folder, and on
    /// Linux and macOS an open file does not stop a delete at all. There .NET turns an open shared with no one into an
    /// advisory lock on the file (flock), which this open asks for in turn.
    /// </summary>
    private static bool InUse(string folder)
    {
        try
        {
            using var free = new FileStream(Path.Combine(folder, LockFile), FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // No lock file: its run ended before it made one, or the folder has just gone.
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Held by its owner, or not Boatdrill's to open: either way not for this sweep.
            return true;
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

/// <summary>A drill's, a rehearsal's or a scan's own folder in the scratch directory, held as in use until disposed.</summary>
internal sealed class ScratchFolder(string fullName, FileStream held) : IDisposable
{
    public string FullName { get; } = fullName;

    /// <summary>Lets go of the lock, then deletes the folder. A file another program held open stays behind; a later sweep removes it.</summary>
    public void Dispose()
    {
        held.Dispose();
        Scratch.TryDelete(FullName);
    }
}
