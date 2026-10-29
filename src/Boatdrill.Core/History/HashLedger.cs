namespace Boatdrill.Core.History;

/// <summary>
/// A copy that read back correctly, remembered so a later drill can read it again and see whether it is still
/// the same. That needs no original: the copy is compared with itself as it was.
/// </summary>
public sealed record LedgerEntry(
    string DestinationId,
    string SourceId,
    string RelativePath,
    string CopyIdentity,
    long CopyLength,
    DateTimeOffset? CopyWriteUtc,
    string CopySha256,
    long SourceLength,
    DateTimeOffset SourceWriteUtc,
    DateTimeOffset VerifiedUtc)
{
    /// <summary>When a re-check first found the copy changed under the same name, size and date. Re-checked every drill until resolved.</summary>
    public DateTimeOffset? RotSeenUtc { get; init; }
}

/// <summary>
/// The hash ledger: copies verified in past drills, per destination. Each drill reads a few of them again,
/// those verified longest ago first, and a copy whose bytes changed while its name, size and date did not has
/// rotted. Over months that checks far more of a backup than one drill's sample can.
/// </summary>
public sealed class HashLedger
{
    public const int MaxEntriesPerDestination = 5000;
    public const int RechecksPerDrill = 25;
    public const long RecheckBytes = 64L * 1024 * 1024;

    /// <summary>A copy verified more recently than this is not worth reading again yet; a daily drill still re-checks yesterday's.</summary>
    public static readonly TimeSpan MinimumAge = TimeSpan.FromHours(20);

    private readonly Dictionary<string, LedgerEntry> _entries = new(KeyComparer);
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

    public HashLedger(IEnumerable<LedgerEntry>? entries = null)
    {
        foreach (var entry in entries ?? []) Record(entry);
    }

    public IReadOnlyCollection<LedgerEntry> Entries => _entries.Values;

    public int Count(string destinationId) => _counts.GetValueOrDefault(destinationId);

    /// <summary>
    /// The copies at a destination to read again this drill: any seen rotting first, then those verified longest
    /// ago, up to <see cref="RechecksPerDrill"/> and <see cref="RecheckBytes"/>. Only sources in this drill
    /// count, and a file in this drill's sample is left out, being read anyway.
    /// </summary>
    /// <param name="sampled">Each source in the drill, by id, with the relative paths sampled from it.</param>
    public IReadOnlyList<LedgerEntry> Due(string destinationId, IReadOnlyDictionary<string, IReadOnlyCollection<string>> sampled, DateTimeOffset now)
    {
        var skip = new HashSet<string>(sampled.SelectMany(s => s.Value.Select(p => Key(destinationId, s.Key, p))), KeyComparer);
        var candidates = _entries.Values
            .Where(e => e.DestinationId == destinationId && sampled.ContainsKey(e.SourceId))
            .Where(e => e.RotSeenUtc is not null || now - e.VerifiedUtc >= MinimumAge)
            .Where(e => !skip.Contains(Key(e)))
            .OrderByDescending(e => e.RotSeenUtc is not null)
            .ThenBy(e => e.VerifiedUtc);

        var due = new List<LedgerEntry>();
        long bytes = 0;
        foreach (var entry in candidates)
        {
            if (bytes + entry.CopyLength > RecheckBytes) continue;
            due.Add(entry);
            bytes += entry.CopyLength;
            if (due.Count == RechecksPerDrill) break;
        }

        return due;
    }

    /// <summary>Adds or replaces the entry for a copy. Past the limit, the copies verified longest ago are forgotten, never one seen rotting.</summary>
    public void Record(LedgerEntry entry)
    {
        var key = Key(entry);
        if (!_entries.ContainsKey(key)) _counts[entry.DestinationId] = Count(entry.DestinationId) + 1;
        _entries[key] = entry;

        if (Count(entry.DestinationId) <= MaxEntriesPerDestination) return;

        var oldest = _entries.Values
            .Where(e => e.DestinationId == entry.DestinationId && e.RotSeenUtc is null)
            .MinBy(e => e.VerifiedUtc);
        if (oldest is not null) Forget(oldest);
    }

    public void Forget(LedgerEntry entry)
    {
        if (_entries.Remove(Key(entry))) _counts[entry.DestinationId] = Count(entry.DestinationId) - 1;
    }

    /// <summary>Forgets every copy at a destination or of a source that is no longer set up.</summary>
    public void Keep(IReadOnlyCollection<string> destinationIds, IReadOnlyCollection<string> sourceIds)
    {
        foreach (var entry in _entries.Values.Where(e => !destinationIds.Contains(e.DestinationId) || !sourceIds.Contains(e.SourceId)).ToList())
        {
            Forget(entry);
        }
    }

    private static string Key(LedgerEntry e) => Key(e.DestinationId, e.SourceId, e.RelativePath);

    private static string Key(string destinationId, string sourceId, string relativePath) =>
        $"{destinationId}|{sourceId}|{relativePath.Replace('\\', '/')}";

    // File names differ only in case on Linux; on Windows they are the same file.
    private static StringComparer KeyComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
