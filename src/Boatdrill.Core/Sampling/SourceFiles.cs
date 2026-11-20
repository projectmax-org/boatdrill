namespace Boatdrill.Core.Sampling;

/// <summary>
/// The files of a source that a drill samples from and a scan goes through: not hidden or system files, not links,
/// not online-only files whose bytes are in the cloud, nothing excluded, and not Boatdrill's own canary.
/// </summary>
internal static class SourceFiles
{
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        // Hidden and system files are mostly churn (desktop.ini, Thumbs.db); reparse points are links, which Boatdrill
        // must never follow. A folder that is recalled on open is a cloud folder whose list of files is not here
        // either: listing it would fetch it.
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint | CloudFiles.RecallOnOpen,
    };

    /// <param name="below">A folder inside the source, by its path below the root, to go through alone; paths stay relative to the root.</param>
    /// <param name="notHere">
    /// Called for each file left out because its bytes are not on this disk, such as a OneDrive file that is online only:
    /// reading it would download it.
    /// </param>
    public static IEnumerable<SampledFile> Enumerate(Source source, string? below = null, Action? notHere = null)
    {
        var root = Path.GetFullPath(source.RootPath);
        var start = string.IsNullOrEmpty(below) ? root : Path.GetFullPath(Path.Combine(root, below));

        // A folder that leads out of the source (rooted, or through "..") would hand out files that are not the source's.
        if (!IsInside(root, start) || !Directory.Exists(start)) yield break;

        var filter = new PathFilter(source.Excludes);
        foreach (var info in new DirectoryInfo(start).EnumerateFiles("*", Options))
        {
            var relative = Path.GetRelativePath(root, info.FullName);
            if (filter.Excludes(relative)) continue;

            // The canary changes with every drill; it is checked on its own, never sampled or counted.
            if (string.Equals(relative, Drill.Canary.FileName, StringComparison.OrdinalIgnoreCase)) continue;

            long length;
            DateTimeOffset written;
            try
            {
                // From the folder listing: none of these opens the file.
                if (CloudFiles.IsNotHere(info.Attributes))
                {
                    notHere?.Invoke();
                    continue;
                }

                length = info.Length;
                written = info.LastWriteTimeUtc;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            yield return new SampledFile(relative, info.FullName, length, written);
        }
    }

    /// <summary>Is the path the root itself, or somewhere below it? Both are full paths.</summary>
    internal static bool IsInside(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." || !(Path.IsPathRooted(relative) || relative == ".." ||
                                    relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                                    relative.StartsWith("../", StringComparison.Ordinal));
    }
}
