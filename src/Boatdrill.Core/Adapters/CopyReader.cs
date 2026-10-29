using System.Diagnostics;
using System.Security.Cryptography;

namespace Boatdrill.Core.Adapters;

/// <summary>Streams a file into a scratch file while hashing it: the read-back every adapter ends with.</summary>
internal static class CopyReader
{
    private const int BufferSize = 1 << 16;

    public static async Task<ReadBack> ReadAsync(string path, string scratchPath, TimeSpan fetchTime, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(scratchPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize,
                FileOptions.Asynchronous);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            var buffer = new byte[BufferSize];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                total += read;
            }

            await output.FlushAsync(ct).ConfigureAwait(false);
            watch.Stop();
            return new ReadBack(true, total, watch.Elapsed + fetchTime, Convert.ToHexStringLower(hash.GetHashAndReset()), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            watch.Stop();
            return new ReadBack(false, 0, watch.Elapsed + fetchTime, null, ex.Message);
        }
    }
}
