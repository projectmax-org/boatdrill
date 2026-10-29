using Boatdrill.Core.Sampling;

using Boatdrill.Core.Text;

namespace Boatdrill.Core.Adapters;

/// <summary>Plain copies in a folder: an external drive, a NAS share, a OneDrive or Dropbox folder.</summary>
public sealed class MirrorFolderAdapter : IDestinationAdapter
{
    // Windows marks a cloud placeholder with these; the FileAttributes enum lacks the last two.
    private const int RecallOnOpen = 0x40000;
    private const int RecallOnDataAccess = 0x400000;

    /// <summary>Sampled files tried against each layout before choosing one.</summary>
    internal const int ProbeFiles = 25;

    public DestinationKind Kind => DestinationKind.MirrorFolder;

    public Task<DestinationProbe> ProbeAsync(Destination destination, CancellationToken ct) => Task.FromResult(ProbeFolder(destination.RootPath));

    internal static DestinationProbe ProbeFolder(string root)
    {
        if (!Directory.Exists(root)) return new DestinationProbe(false, Language.Pick($"{root} was not found", $"{Korean.Object(root)} 찾을 수 없습니다"), null);

        long? free = null;
        try
        {
            free = new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // Network paths and unusual mounts give no free-space figure; the folder is still reachable.
        }

        return new DestinationProbe(true, Language.Pick($"{root} is reachable", $"{root}에 연결됩니다"), free);
    }

    public Task<SourceBinding> BindAsync(Destination destination, Sample sample, string scratchDirectory, BindPurpose purpose,
        IReadOnlyCollection<string> alsoRead, CancellationToken ct)
    {
        var candidates = Candidates(destination, sample.Source).ToList();
        if (destination.Layout != MirrorLayout.Auto)
        {
            var (layout, basePath) = candidates.First(c => c.Layout == destination.Layout);
            return Task.FromResult(new SourceBinding(destination, sample.Source, layout, basePath, 0, 0));
        }

        var probe = sample.Files.Take(ProbeFiles).ToList();
        SourceBinding? best = null;
        foreach (var (layout, basePath) in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var hits = probe.Count(f => File.Exists(Path.Combine(basePath, f.RelativePath)));
            if (best is null || hits > best.ProbeHits)
            {
                best = new SourceBinding(destination, sample.Source, layout, basePath, hits, probe.Count);
            }
        }

        return Task.FromResult(best!);
    }

    /// <summary>The layouts tried, in order; ties go to the earlier one.</summary>
    internal static IEnumerable<(MirrorLayout Layout, string BasePath)> Candidates(Destination destination, Source source)
    {
        var root = Path.GetFullPath(destination.RootPath);
        var sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.RootPath));
        yield return (MirrorLayout.Contents, root);
        yield return (MirrorLayout.RootFolder, Path.Combine(root, Source.DisplayName(sourceRoot)));
        yield return (MirrorLayout.FullPath, Path.Combine(root, VolumeFolder(sourceRoot), PathBelowVolume(sourceRoot)));
    }

    /// <summary>"C:\Users\max" gives "C"; "/home/max" gives "root". The folder a full-path copy puts a volume under.</summary>
    internal static string VolumeFolder(string fullPath)
    {
        var volume = Path.GetPathRoot(fullPath) ?? string.Empty;
        var name = volume.Replace(":", string.Empty).Trim('\\', '/');
        return string.IsNullOrEmpty(name) ? "root" : name;
    }

    internal static string PathBelowVolume(string fullPath)
    {
        var volume = Path.GetPathRoot(fullPath) ?? string.Empty;
        return fullPath[volume.Length..].TrimStart('\\', '/');
    }

    public Task<CopyLookup> LocateAsync(SourceBinding binding, string relativePath, CancellationToken ct) =>
        Task.FromResult(LocateFile(Path.Combine(binding.BasePath, relativePath)));

    internal static CopyLookup LocateFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return CopyLookup.Missing(path);

            var attributes = (int)info.Attributes;
            if ((attributes & (RecallOnDataAccess | RecallOnOpen)) != 0 || info.Attributes.HasFlag(FileAttributes.Offline))
            {
                return new CopyLookup(CopyState.Placeholder, path, info.Length, info.LastWriteTimeUtc, Language.Pick("cloud placeholder: the bytes are not on this disk", "온라인 전용 파일: 내용이 이 디스크에 없습니다"));
            }

            return new CopyLookup(CopyState.Found, path, info.Length, info.LastWriteTimeUtc, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CopyLookup.Failed(path, ex.Message);
        }
    }

    public Task<ReadBack> ReadBackAsync(SourceBinding binding, CopyLookup copy, string scratchPath, CancellationToken ct) =>
        copy.State != CopyState.Found || copy.Path is null
            ? Task.FromResult(new ReadBack(false, 0, TimeSpan.Zero, null, Language.Pick("no copy to read", "읽을 사본이 없습니다")))
            : CopyReader.ReadAsync(copy.Path, scratchPath, copy.FetchTime, ct);
}
