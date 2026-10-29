namespace Boatdrill.Core.Sampling;

/// <summary>
/// The files of a source that a drill samples from and a scan goes through: not hidden or system files, not
/// links or cloud placeholders, nothing excluded, and not Boatdrill's own canary.
/// </summary>
internal static class SourceFiles
{
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        // Hidden and system files are mostly churn (desktop.ini, Thumbs.db); reparse points are links and cloud
        // placeholders, which Boatdrill must never follow or pull down.
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
    };

    public static IEnumerable<SampledFile> Enumerate(Source source)
    {
        var root = Path.GetFullPath(source.RootPath);
        if (!Directory.Exists(root)) yield break;

        var filter = new PathFilter(source.Excludes);
        foreach (var info in new DirectoryInfo(root).EnumerateFiles("*", Options))
        {
            var relative = Path.GetRelativePath(root, info.FullName);
            if (filter.Excludes(relative)) continue;

            // The canary changes with every drill; it is checked on its own, never sampled or counted.
            if (string.Equals(relative, Drill.Canary.FileName, StringComparison.OrdinalIgnoreCase)) continue;

            long length;
            DateTimeOffset written;
            try
            {
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
}
