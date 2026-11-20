using System.Collections.Concurrent;

namespace Boatdrill.Core;

/// <summary>
/// Where paths really lead, with every junction and symbolic link on the way followed, so that a folder reached by
/// two names is known for one folder. A backup folder that is a link to the protected folder holds no copy of it,
/// only the originals again, and reading those back would compare each file with itself.
/// <para>
/// A folder is asked once whether it is a link and the answer is kept, so one of these serves a whole binding. Some
/// names take calls that only Windows has to see through: a mapped or subst drive, a volume mounted in a folder,
/// \\localhost\c$. And a hard link is the same file under another name, with nothing in its path to tell it by.
/// Those are compared as they are spelled.
/// </para>
/// </summary>
internal sealed class RealPaths
{
    /// <summary>Links followed on the way to one path before it is taken for a loop.</summary>
    private const int MostLinks = 40;

    private readonly ConcurrentDictionary<string, string> _folders = new(StringComparer.Ordinal);

    /// <summary>Windows and macOS do not tell names apart by case, and neither does .NET where it compares paths there.</summary>
    public static StringComparison Comparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>A folder's full path with the links in it followed; as it is spelled, from wherever that cannot be told.</summary>
    public string Of(string folder)
    {
        if (_folders.TryGetValue(folder, out var known)) return known;

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        string real;
        try
        {
            var links = MostLinks;
            real = Follow(full, ref links);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            real = full;
        }

        return _folders[folder] = real;
    }

    /// <summary>Whether two folders are one and the same.</summary>
    public bool Same(string one, string other) => string.Equals(Of(one), Of(other), Comparison);

    /// <summary>
    /// Where a file really is: in the folder that its folder really is, or, when the file is itself a link, wherever
    /// that leads. Folders are remembered and files are not, for a backup holds many files to a folder.
    /// </summary>
    /// <param name="isLink">The file is marked as a link where it is listed, so it is asked where it leads; any other file is not.</param>
    public string OfFile(string file, bool isLink)
    {
        var full = Path.GetFullPath(file);
        try
        {
            for (var links = MostLinks; ; links--)
            {
                var folder = Of(Path.GetDirectoryName(full) ?? full);
                var real = Path.Combine(folder, Path.GetFileName(full));
                if (!isLink || links == 0 || new FileInfo(real).LinkTarget is not { Length: > 0 } target) return real;

                full = Path.GetFullPath(target, folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return full;
        }
    }

    private string Follow(string full, ref int links)
    {
        if (_folders.TryGetValue(full, out var known)) return known;

        // A drive or a share has nothing above it that could be a link.
        if (Path.GetDirectoryName(full) is not { Length: > 0 } parent) return full;

        var above = Follow(parent, ref links);
        var real = Path.Combine(above, Path.GetFileName(full));
        if (new FileInfo(real).LinkTarget is { Length: > 0 } target && links-- > 0)
        {
            // A target that is not a full path starts from the folder the link is in, and may hold links of its own.
            real = Follow(Path.TrimEndingDirectorySeparator(Path.GetFullPath(target, above)), ref links);
        }

        return _folders[full] = real;
    }
}
