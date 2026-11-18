namespace Boatdrill.Core.Tests;

/// <summary>A throwaway folder tree under the temp directory, deleted when disposed.</summary>
internal sealed class TempTree : IDisposable
{
    public string Root { get; } = Directory.CreateDirectory(
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "boatdrill-tests", Guid.NewGuid().ToString("N"))).FullName;

    public string At(string relative) => System.IO.Path.Combine(Root, relative);

    public string Write(string relative, string content, DateTimeOffset? written = null)
    {
        var full = At(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        if (written is { } when) File.SetLastWriteTimeUtc(full, when.UtcDateTime);
        return full;
    }

    public string Write(string relative, byte[] content, DateTimeOffset? written = null)
    {
        var full = At(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        if (written is { } when) File.SetLastWriteTimeUtc(full, when.UtcDateTime);
        return full;
    }

    /// <summary>Copies every file into another root, keeping write times, the way a mirror backup does.</summary>
    public void MirrorTo(string destinationRoot, IEnumerable<string>? skip = null)
    {
        var skipped = new HashSet<string>((skip ?? []).Select(Normalise), StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            var relative = System.IO.Path.GetRelativePath(Root, file);
            if (skipped.Contains(Normalise(relative))) continue;

            var target = System.IO.Path.Combine(destinationRoot, relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(file));
        }
    }

    private static string Normalise(string path) => path.Replace('\\', '/');

    /// <summary>Fails naming everything left in a folder, with its attributes, where Assert.Empty shows a few characters of the first.</summary>
    public static void AssertEmpty(string folder)
    {
        var left = Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(p => $"{p} ({File.GetAttributes(p)})")
            .ToList();
        if (left.Count > 0) Assert.Fail($"Left in {folder}:\n{string.Join('\n', left)}");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
