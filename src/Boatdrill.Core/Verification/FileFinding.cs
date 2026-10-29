using System.Text.Json.Serialization;

namespace Boatdrill.Core.Verification;

public enum FileOutcome
{
    /// <summary>A copy exists and its bytes match the original.</summary>
    Verified,

    /// <summary>A copy exists but is older than the original: the backup has not caught up.</summary>
    Outdated,

    /// <summary>A copy exists and is not older than the original, yet its bytes differ. Corruption, or a bad copy.</summary>
    Corrupt,

    /// <summary>No copy at the expected place.</summary>
    Missing,

    /// <summary>A cloud placeholder: the name is there, the bytes are not on this disk.</summary>
    Placeholder,

    /// <summary>The copy or the original could not be read.</summary>
    Error,

    /// <summary>
    /// A copy verified in an earlier drill now holds other bytes, though its name, size and date are the same:
    /// bit rot, or something that wrote to the backup behind the backup tool's back.
    /// </summary>
    Rotted,
}

/// <summary>What a drill found out about one sampled file at one destination.</summary>
public sealed record FileFinding(
    string RelativePath,
    FileOutcome Outcome,
    long SourceLength,
    DateTimeOffset SourceWriteUtc,
    long? CopyLength,
    DateTimeOffset? CopyWriteUtc,
    string? Detail)
{
    /// <summary>Found by reading again a copy verified in an earlier drill, not by this drill's sample.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Recheck { get; init; }
}
