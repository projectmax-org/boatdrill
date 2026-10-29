using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Boatdrill.Core.Sampling;

using Boatdrill.Core.Text;

namespace Boatdrill.Core.Adapters;

/// <summary>
/// A Windows File History drive. File History keeps every version of a file beside the others, with the
/// moment it was taken in the name: <c>FileHistory\max\LAPTOP\Data\C\Users\max\Documents\Report (2026_10_28 14_00_05 UTC).docx</c>.
/// The newest version is the one a restore would bring back, so that is the one read.
/// </summary>
public sealed partial class FileHistoryAdapter : IDestinationAdapter
{
    public DestinationKind Kind => DestinationKind.FileHistory;

    public Task<DestinationProbe> ProbeAsync(Destination destination, CancellationToken ct)
    {
        var probe = MirrorFolderAdapter.ProbeFolder(destination.RootPath);
        if (probe.Reachable && DataFolders(destination.RootPath).Count == 0)
        {
            return Task.FromResult(new DestinationProbe(false, Language.Pick($"{destination.RootPath} holds no File History data", $"{destination.RootPath}에 파일 히스토리 데이터가 없습니다"), probe.FreeBytes));
        }

        return Task.FromResult(probe);
    }

    public Task<SourceBinding> BindAsync(Destination destination, Sample sample, string scratchDirectory, BindPurpose purpose,
        IReadOnlyCollection<string> alsoRead, CancellationToken ct)
    {
        var sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sample.Source.RootPath));
        var below = Path.Combine(MirrorFolderAdapter.VolumeFolder(sourceRoot), MirrorFolderAdapter.PathBelowVolume(sourceRoot));
        var probe = sample.Files.Take(MirrorFolderAdapter.ProbeFiles).ToList();

        SourceBinding? best = null;
        foreach (var data in DataFolders(destination.RootPath))
        {
            ct.ThrowIfCancellationRequested();
            var state = new Listings();
            var basePath = Path.Combine(data, below);
            var hits = probe.Count(f => Newest(state, basePath, f.RelativePath) is not null);
            if (best is null || hits > best.ProbeHits)
            {
                best = new SourceBinding(destination, sample.Source, MirrorLayout.FullPath, basePath, hits, probe.Count) { State = state };
            }
        }

        return Task.FromResult(best ?? new SourceBinding(destination, sample.Source, MirrorLayout.FullPath, destination.RootPath, 0, probe.Count)
        {
            State = new Listings(),
            Problem = Language.Pick("no File History data was found on " + destination.RootPath, $"{destination.RootPath}에서 파일 히스토리 데이터를 찾지 못했습니다"),
        });
    }

    /// <summary>
    /// Every FileHistory\user\PC\Data folder reachable from the chosen folder, which may be the drive itself,
    /// its FileHistory folder, a user's or a PC's folder within it, or a Data folder.
    /// </summary>
    internal static IReadOnlyList<string> DataFolders(string root)
    {
        if (!Directory.Exists(root)) return [];

        var found = new List<string>();
        void Look(string directory, int depth)
        {
            if (Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)).Equals("Data", StringComparison.OrdinalIgnoreCase))
            {
                found.Add(directory);
                return;
            }

            if (depth == 0) return;
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                // Below the drive, only the FileHistory folder; inside it, any user and PC folder.
                if (depth == 4 && !name.Equals("FileHistory", StringComparison.OrdinalIgnoreCase)) continue;
                Look(child, depth - 1);
            }
        }

        var rootName = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
        var start = rootName.Equals("Data", StringComparison.OrdinalIgnoreCase) ? 0
            : rootName.Equals("FileHistory", StringComparison.OrdinalIgnoreCase) ? 3
            : Directory.Exists(Path.Combine(root, "FileHistory")) ? 4
            : 2;
        Look(root, start);
        return found;
    }

    public Task<CopyLookup> LocateAsync(SourceBinding binding, string relativePath, CancellationToken ct)
    {
        var state = binding.State as Listings ?? new Listings();
        try
        {
            if (Newest(state, binding.BasePath, relativePath) is not { } version)
            {
                return Task.FromResult(CopyLookup.Missing(Path.Combine(binding.BasePath, relativePath),
                    Language.Pick("File History holds no version of it under ", "파일 히스토리에 이 파일의 버전이 없습니다: ") + Path.Combine(binding.BasePath, Path.GetDirectoryName(relativePath) ?? string.Empty)));
            }

            var info = new FileInfo(version.Path);
            return Task.FromResult(new CopyLookup(CopyState.Found, version.Path, info.Length, version.TakenUtc, null));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(CopyLookup.Failed(Path.Combine(binding.BasePath, relativePath), ex.Message));
        }
    }

    public Task<ReadBack> ReadBackAsync(SourceBinding binding, CopyLookup copy, string scratchPath, CancellationToken ct) =>
        copy.State != CopyState.Found || copy.Path is null
            ? Task.FromResult(new ReadBack(false, 0, TimeSpan.Zero, null, Language.Pick("no copy to read", "읽을 사본이 없습니다")))
            : CopyReader.ReadAsync(copy.Path, scratchPath, copy.FetchTime, ct);

    internal sealed record Version(string Path, DateTimeOffset TakenUtc);

    /// <summary>The newest version of a file, or null. Reported with the moment File History took it, which is what "older than the original" is judged by.</summary>
    private static Version? Newest(Listings listings, string basePath, string relativePath)
    {
        var directory = Path.Combine(basePath, Path.GetDirectoryName(relativePath) ?? string.Empty);
        var name = Path.GetFileName(relativePath);
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);

        Version? newest = null;
        foreach (var candidate in listings.Get(directory))
        {
            if (Parse(candidate, stem, extension) is not { } taken) continue;
            if (newest is null || taken > newest.TakenUtc) newest = new Version(Path.Combine(directory, candidate), taken);
        }

        return newest;
    }

    /// <summary>The moment in "stem (2026_10_28 14_00_05 UTC).ext", when the name is a version of stem.ext.</summary>
    internal static DateTimeOffset? Parse(string fileName, string stem, string extension)
    {
        var comparison = StringComparison.OrdinalIgnoreCase;
        if (!fileName.StartsWith(stem + " (", comparison) || !fileName.EndsWith(" UTC)" + extension, comparison)) return null;

        var middle = fileName.Substring(stem.Length + 2, fileName.Length - stem.Length - 2 - (" UTC)" + extension).Length);
        return Stamp().IsMatch(middle) &&
               DateTimeOffset.TryParseExact(middle, "yyyy'_'MM'_'dd HH'_'mm'_'ss", CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var taken)
            ? taken
            : null;
    }

    /// <summary>The name File History gives the version of stem.ext taken at a moment.</summary>
    internal static string VersionName(string fileName, DateTimeOffset takenUtc) =>
        $"{Path.GetFileNameWithoutExtension(fileName)} ({takenUtc.UtcDateTime:yyyy'_'MM'_'dd HH'_'mm'_'ss} UTC){Path.GetExtension(fileName)}";

    [GeneratedRegex(@"^\d{4}_\d{2}_\d{2} \d{2}_\d{2}_\d{2}$")]
    private static partial Regex Stamp();

    /// <summary>Directory listings, each read once per binding: a folder of photos holds many files and many versions of each.</summary>
    internal sealed class Listings
    {
        private readonly ConcurrentDictionary<string, string[]> _cache = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        public string[] Get(string directory) => _cache.GetOrAdd(directory, d =>
        {
            try
            {
                return Directory.Exists(d) ? Directory.GetFiles(d).Select(Path.GetFileName).OfType<string>().ToArray() : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        });
    }
}
