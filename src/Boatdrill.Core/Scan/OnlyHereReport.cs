namespace Boatdrill.Core.Scan;

/// <summary>A file whose current version no backup that was reached holds.</summary>
/// <param name="HasOlderCopy">Some backup holds a copy, but from before the file last changed.</param>
public sealed record OnlyHereFile(string SourceName, string RelativePath, long Length, DateTimeOffset LastWriteUtc, bool HasOlderCopy);

/// <summary>A folder, by its path below the source root, and how much in it exists only on this PC.</summary>
public sealed record OnlyHereFolder(string SourceName, string RelativePath, int Files, long Bytes);

/// <summary>One source's side of a scan: everything in it, and what has no current copy anywhere.</summary>
public sealed record OnlyHereSource(string SourceId, string SourceName, string RootPath)
{
    public long Files { get; init; }

    public long Bytes { get; init; }

    /// <summary>Files no backup holds at all.</summary>
    public long NoCopyFiles { get; init; }

    public long NoCopyBytes { get; init; }

    /// <summary>Files every backup holds only from before they last changed.</summary>
    public long OlderFiles { get; init; }

    public long OlderBytes { get; init; }
}

/// <summary>
/// What exists only on this PC: every file in the sources looked up in every backup that could be reached,
/// by name and date. Nothing is read back; the drill is what proves copies are good.
/// </summary>
public sealed record OnlyHereReport
{
    public required DateTimeOffset StartedUtc { get; init; }

    public required DateTimeOffset FinishedUtc { get; init; }

    /// <summary>The backups looked in, by name.</summary>
    public required IReadOnlyList<string> Checked { get; init; }

    /// <summary>The backups that could not be looked in, each as "name: why": not reached, or their files not listed.</summary>
    public required IReadOnlyList<string> Unreachable { get; init; }

    /// <summary>
    /// The sources, by name, that no backup could be looked in for. Their files are not counted here, which must not
    /// read as their having copies.
    /// </summary>
    public IReadOnlyList<string> NotCompared { get; init; } = [];

    public required IReadOnlyList<OnlyHereSource> Sources { get; init; }

    /// <summary>The folders holding the most bytes that exist only here, largest first.</summary>
    public required IReadOnlyList<OnlyHereFolder> Folders { get; init; }

    /// <summary>The largest files that exist only here, largest first.</summary>
    public required IReadOnlyList<OnlyHereFile> Largest { get; init; }

    public required string Sentence { get; init; }

    public long OnlyHereFiles => Sources.Sum(s => s.NoCopyFiles + s.OlderFiles);

    public long OnlyHereBytes => Sources.Sum(s => s.NoCopyBytes + s.OlderBytes);
}

public sealed record ScanProgress(string Stage, long FilesChecked, long OnlyHereFiles);
