using System.Text.Json;

namespace Boatdrill.Core.History;

/// <summary>The hash ledger on disk: one JSON file, replaced whole after each drill.</summary>
public sealed class LedgerStore(string filePath)
{
    public string FilePath { get; } = filePath;

    public HashLedger Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new HashLedger();

            using var stream = File.OpenRead(FilePath);
            var file = JsonSerializer.Deserialize<LedgerFile>(stream, BoatdrillJson.Options);
            return new HashLedger(file?.Entries ?? []);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A damaged ledger costs only the re-checks it held; the next drills fill it again.
            return new HashLedger();
        }
    }

    public async Task SaveAsync(HashLedger ledger, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temporary = FilePath + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, new LedgerFile(1, [.. ledger.Entries]), BoatdrillJson.Options, ct).ConfigureAwait(false);
        }

        File.Move(temporary, FilePath, overwrite: true);
    }

    private sealed record LedgerFile(int Version, IReadOnlyList<LedgerEntry> Entries);
}
