using System.Text.RegularExpressions;
using Boatdrill.Core.Text;

namespace Boatdrill.Core;

public enum DestinationKind
{
    /// <summary>A folder that holds plain copies: an external drive, a NAS share, a OneDrive or Dropbox folder.</summary>
    MirrorFolder,

    /// <summary>A Windows File History drive: versions named "name (2026_10_28 14_00_00 UTC).ext" under FileHistory\user\PC\Data.</summary>
    FileHistory,

    /// <summary>A restic repository, read through the restic command-line tool.</summary>
    Restic,

    /// <summary>An rclone remote (Google Drive, OneDrive, S3, B2 and rclone's other back ends), read through rclone itself.</summary>
    Rclone,

    /// <summary>Zip or 7z archives in a folder, the newest that holds a folder's files read as its backup; or one archive.</summary>
    Archive,
}

/// <summary>How a mirror destination lays out the copies of one source.</summary>
public enum MirrorLayout
{
    /// <summary>Work it out from the sample: try the others and keep the one that finds the most files.</summary>
    Auto,

    /// <summary>The destination root holds the source folder's contents directly: D:\Backup ↔ C:\Users\max\Documents.</summary>
    Contents,

    /// <summary>The destination root holds a folder named after the source: D:\Backup\Documents ↔ C:\Users\max\Documents.</summary>
    RootFolder,

    /// <summary>The destination mirrors the full path below a volume folder: D:\Backup\C\Users\max\Documents.</summary>
    FullPath,
}

/// <summary>A place where copies are supposed to exist. Boatdrill only ever reads from it.</summary>
public sealed partial record Destination(string Id, string Name, DestinationKind Kind, string RootPath)
{
    public MirrorLayout Layout { get; init; } = MirrorLayout.Auto;

    /// <summary>A repository's password, already decrypted. Held in memory for a drill; Core never writes it anywhere.</summary>
    public string? Secret { get; init; }

    /// <summary>The command-line tool a repository is read with, when it is not on the PATH.</summary>
    public string? ToolPath { get; init; }

    public static Destination MirrorFolder(string rootPath, string? name = null, MirrorLayout layout = MirrorLayout.Auto)
    {
        var full = Path.GetFullPath(rootPath);
        return new Destination(IdFor(DestinationKind.MirrorFolder, full), name ?? DefaultName(DestinationKind.MirrorFolder, full), DestinationKind.MirrorFolder, full) { Layout = layout };
    }

    public static Destination FileHistory(string rootPath, string? name = null)
    {
        var full = Path.GetFullPath(rootPath);
        return new Destination(IdFor(DestinationKind.FileHistory, full), name ?? DefaultName(DestinationKind.FileHistory, full), DestinationKind.FileHistory, full);
    }

    public static Destination Restic(string repository, string? password, string? name = null, string? toolPath = null)
    {
        var location = NormaliseLocation(DestinationKind.Restic, repository);
        return new Destination(IdFor(DestinationKind.Restic, location), name ?? DefaultName(DestinationKind.Restic, location), DestinationKind.Restic, location)
        {
            Secret = password,
            ToolPath = toolPath,
        };
    }

    /// <param name="location">A remote and a path as rclone writes them, such as "gdrive:Backups/Laptop".</param>
    /// <param name="configPassword">The password of an encrypted rclone config, if it is encrypted.</param>
    public static Destination Rclone(string location, string? configPassword = null, string? name = null, string? toolPath = null)
    {
        var normalised = NormaliseLocation(DestinationKind.Rclone, location);
        return new Destination(IdFor(DestinationKind.Rclone, normalised), name ?? DefaultName(DestinationKind.Rclone, normalised), DestinationKind.Rclone, normalised)
        {
            Secret = configPassword,
            ToolPath = toolPath,
        };
    }

    /// <param name="location">A folder of zip or 7z archives, or one archive.</param>
    /// <param name="toolPath">7-Zip's 7z, for 7z archives, when it is neither on the PATH nor in Program Files.</param>
    public static Destination Archive(string location, string? name = null, string? toolPath = null)
    {
        var full = Path.GetFullPath(location);
        return new Destination(IdFor(DestinationKind.Archive, full), name ?? DefaultName(DestinationKind.Archive, full), DestinationKind.Archive, full)
        {
            ToolPath = toolPath,
        };
    }

    /// <summary>The same id for the same place on every run, so history lines up.</summary>
    public static string IdFor(DestinationKind kind, string location)
    {
        var normalised = NormaliseLocation(kind, location);
        return kind switch
        {
            DestinationKind.Restic when IsRepositoryUrl(normalised) => Source.IdForText("restic:" + normalised),
            DestinationKind.Rclone => Source.IdForText("rclone:" + normalised),
            DestinationKind.Archive => Source.IdForText("archive:" + normalised),
            _ => Source.IdForPath(normalised),
        };
    }

    /// <summary>The last folder of a path, or the end of a repository URL.</summary>
    public static string DefaultName(DestinationKind kind, string location)
    {
        if ((kind == DestinationKind.Restic && IsRepositoryUrl(location)) || kind == DestinationKind.Rclone)
        {
            var tail = location.TrimEnd('/', '\\');
            var cut = tail.LastIndexOfAny(['/', '\\', ':']);
            return cut >= 0 && cut < tail.Length - 1 ? tail[(cut + 1)..] : tail;
        }

        return Source.DisplayName(location);
    }

    /// <summary>A folder is made absolute; a repository URL such as sftp:host:/srv/restic is kept as it is.</summary>
    public static string NormaliseLocation(DestinationKind kind, string location) =>
        kind == DestinationKind.Rclone || (kind == DestinationKind.Restic && IsRepositoryUrl(location)) ? location.Trim() : Path.GetFullPath(location);

    /// <summary>restic's remote back ends; anything else is a folder.</summary>
    public static bool IsRepositoryUrl(string location) => RepositoryUrl().IsMatch(location);

    [GeneratedRegex(@"^(sftp|rest|s3|b2|azure|gs|swift|rclone):", RegexOptions.IgnoreCase)]
    private static partial Regex RepositoryUrl();

    /// <summary>The location to show and to keep in history: a password written into a repository URL is masked.</summary>
    public string DisplayLocation => Redact(RootPath);

    /// <summary>rest:https://max:secret@nas:8000/ becomes rest:https://max:***@nas:8000/.</summary>
    public static string Redact(string location) => UrlPassword().Replace(location, "***");

    /// <summary>Where a backup is, in words: "File History on F:\", "restic repository at …", "Archives in …".</summary>
    public static string Describe(DestinationKind kind, string location) => Language.IsKorean
        ? kind switch
        {
            DestinationKind.FileHistory => $"{location}의 파일 히스토리",
            DestinationKind.Restic => $"{Redact(location)}의 restic 저장소",
            DestinationKind.Rclone => $"{location}의 rclone 원격 저장소",
            DestinationKind.Archive when IsArchiveFile(location) => location,
            DestinationKind.Archive => $"{location}의 압축 파일",
            _ => location,
        }
        : kind switch
        {
            DestinationKind.FileHistory => "File History on " + location,
            DestinationKind.Restic => "restic repository at " + Redact(location),
            DestinationKind.Rclone => "rclone remote at " + location,
            DestinationKind.Archive when IsArchiveFile(location) => location,
            DestinationKind.Archive => "Archives in " + location,
            _ => location,
        };

    /// <summary>A zip or 7z archive, by its name.</summary>
    public static bool IsArchiveFile(string path) =>
        Path.GetExtension(path) is var extension &&
        (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) || extension.Equals(".7z", StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"(?<=://[^/@:\s]+:)[^/@\s]+(?=@)")]
    private static partial Regex UrlPassword();

    /// <summary>Never shows the secret, whatever else prints a destination.</summary>
    public override string ToString() => $"{Kind} {Name} at {DisplayLocation}";
}
