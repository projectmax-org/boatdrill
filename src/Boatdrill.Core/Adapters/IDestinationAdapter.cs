using Boatdrill.Core.Sampling;

namespace Boatdrill.Core.Adapters;

public enum CopyState
{
    Found,
    Missing,
    /// <summary>A cloud placeholder: the name is on the disk, the bytes are not.</summary>
    Placeholder,
    Error,
}

/// <summary>What a binding is for: a drill reads copies back; a scan only asks which exist.</summary>
public enum BindPurpose
{
    Read,
    ListOnly,
}

/// <summary>What a destination knows about the copy of one file before any byte is read.</summary>
public sealed record CopyLookup(CopyState State, string? Path, long Length, DateTimeOffset? LastWriteUtc, string? Detail)
{
    /// <summary>Time already spent fetching this copy (a repository restores in batches), counted as part of reading it.</summary>
    public TimeSpan FetchTime { get; init; }

    /// <summary>
    /// A name for the copy that stays the same while the backup keeps the same copy, for the hash ledger.
    /// Defaults to <see cref="Path"/>; an adapter whose path is a scratch file says what the copy really is.
    /// </summary>
    public string? Identity { get; init; }

    public string? StableIdentity => Identity ?? Path;

    public static CopyLookup Missing(string? path, string? detail = null) => new(CopyState.Missing, path, 0, null, detail);

    public static CopyLookup Failed(string? path, string detail) => new(CopyState.Error, path, 0, null, detail);
}

public sealed record DestinationProbe(bool Reachable, string Detail, long? FreeBytes);

/// <summary>Where one source's copies live inside a destination, and how sure the adapter is about it.</summary>
public sealed record SourceBinding(Destination Destination, Source Source, MirrorLayout Layout, string BasePath, int ProbeHits, int ProbeTried)
{
    /// <summary>Whatever the adapter keeps between binding and reading: a directory cache, a snapshot's index.</summary>
    public object? State { get; init; }

    /// <summary>Why nothing can be found, when the adapter knows: "no snapshot in the repository covers it".</summary>
    public string? Problem { get; init; }
}

public sealed record ReadBack(bool Success, long BytesRead, TimeSpan Elapsed, string? Sha256, string? Detail)
{
    /// <summary>The copy is there but fails the backup tool's own check of its bytes: corruption, not a read error.</summary>
    public bool Damaged { get; init; }
}

/// <summary>
/// Everything Boatdrill knows about one kind of destination. An adapter only ever reads from the
/// destination; the places it may write are the scratch folder and the scratch files it is handed.
/// </summary>
public interface IDestinationAdapter
{
    DestinationKind Kind { get; }

    /// <summary>Is the destination there at all right now, and can it be read?</summary>
    Task<DestinationProbe> ProbeAsync(Destination destination, CancellationToken ct);

    /// <summary>
    /// The tool behind the destination checks every byte it reads against its own record (restic does), so a
    /// copy that reads back without complaint is as it was stored, and a different hash means a new version.
    /// </summary>
    bool ChecksItsOwnBytes => false;

    /// <summary>
    /// Works out where this source's copies live in the destination. For <see cref="BindPurpose.Read"/> the
    /// adapter may also fetch the sampled files ahead of time into <paramref name="scratchDirectory"/>, together
    /// with <paramref name="alsoRead"/>: other files below the source root the drill will read back.
    /// </summary>
    Task<SourceBinding> BindAsync(Destination destination, Sample sample, string scratchDirectory, BindPurpose purpose,
        IReadOnlyCollection<string> alsoRead, CancellationToken ct);

    /// <summary>Finds the copy of one file, by its path below the source root, without reading it.</summary>
    Task<CopyLookup> LocateAsync(SourceBinding binding, string relativePath, CancellationToken ct);

    /// <summary>Restores the copy into the scratch file, hashing the bytes as they stream through.</summary>
    Task<ReadBack> ReadBackAsync(SourceBinding binding, CopyLookup copy, string scratchPath, CancellationToken ct);
}
