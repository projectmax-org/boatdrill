using System.Security.Cryptography;
using System.Text;

namespace Boatdrill.Core.Tests;

/// <summary>Every folder and file under a root with its write time, and each file's size and hash: to show a drill left a repository alone.</summary>
internal static class TreeFingerprint
{
    public static string Of(string root)
    {
        var text = new StringBuilder();
        foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos("*", SearchOption.AllDirectories).OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            // A listing hands back its own copy of each entry's times, and on NTFS a folder's copy can lag behind the
            // folder (FindFirstFile's documented caveat): after restic deleted its lock, the repository's listing still
            // showed the locks folder's earlier write time. Ask each entry itself.
            entry.Refresh();
            text.Append(Path.GetRelativePath(root, entry.FullName)).Append(' ').Append(entry.LastWriteTimeUtc.Ticks);
            if (entry is FileInfo file) text.Append(' ').Append(file.Length).Append(' ').Append(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file.FullName))));
            text.AppendLine();
        }

        return text.ToString();
    }

    /// <summary>
    /// The fingerprint once it holds still for a second: a second guard against the lag described in <see cref="Of"/>,
    /// for a tool that has exited a moment ago.
    /// </summary>
    public static string Settled(string root)
    {
        var last = Of(root);
        for (var i = 0; i < 15; i++)
        {
            Thread.Sleep(TimeSpan.FromSeconds(1));
            var now = Of(root);
            if (now == last) return now;
            last = now;
        }

        return last;
    }
}
