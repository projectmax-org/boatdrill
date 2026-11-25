using Boatdrill.Core.Sampling;

using Boatdrill.Core.Text;

namespace Boatdrill.Core.Adapters;

/// <summary>Plain copies in a folder: an external drive, a NAS share, a OneDrive or Dropbox folder.</summary>
public sealed class MirrorFolderAdapter : IDestinationAdapter
{
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
        // A layout that leads back to the protected folder would compare every file with itself and find them all. It is
        // told by where the folders really are: one reached through a junction or a symbolic link is no backup either.
        var real = new RealPaths();
        var all = Candidates(destination, sample.Source).ToList();
        var candidates = all.Where(c => !IsTheSource(c.BasePath, sample.Source, real)).ToList();
        if (destination.Layout != MirrorLayout.Auto)
        {
            var (layout, basePath) = all.First(c => c.Layout == destination.Layout);
            return Task.FromResult(IsTheSource(basePath, sample.Source, real)
                ? Unusable(destination, sample, layout, basePath)
                : new SourceBinding(destination, sample.Source, layout, basePath, 0, 0) { State = real });
        }

        var probe = sample.Files.Take(ProbeFiles).ToList();
        SourceBinding? best = null;
        foreach (var (layout, basePath) in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var hits = probe.Count(f => File.Exists(Path.Combine(basePath, f.RelativePath)));
            if (best is null || hits > best.ProbeHits)
            {
                best = new SourceBinding(destination, sample.Source, layout, basePath, hits, probe.Count) { State = real };
            }
        }

        if (best is null) return Task.FromResult(Unusable(destination, sample, MirrorLayout.Contents, Path.GetFullPath(destination.RootPath)));

        // Nothing was found where it looked, and a place where it would not look is the protected folder itself: the
        // drill says that, which tells more than that no copy was found.
        var itself = all.Except(candidates).Select(c => c.BasePath).FirstOrDefault();
        return Task.FromResult(best.ProbeHits == 0 && itself is not null ? best with { Problem = NotACopy(itself) } : best);
    }

    /// <summary>The layouts tried, in order; ties go to the earlier one.</summary>
    internal static IEnumerable<(MirrorLayout Layout, string BasePath)> Candidates(Destination destination, Source source)
    {
        var root = Path.GetFullPath(destination.RootPath);
        var sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.RootPath));

        // A whole drive's folder is named after its letter: E:\Backup\D for D:\. Its "name" is the path D:\ itself,
        // which Path.Combine would take for the whole path, and that is the source.
        var below = PathBelowVolume(sourceRoot);
        yield return (MirrorLayout.Contents, root);
        yield return (MirrorLayout.RootFolder, Path.Combine(root, below.Length == 0 ? VolumeFolder(sourceRoot) : Source.DisplayName(sourceRoot)));
        yield return (MirrorLayout.FullPath, Path.Combine(root, VolumeFolder(sourceRoot), below));
    }

    /// <summary>
    /// Whether a folder is the protected folder itself, which can never be its own backup: by its path as it is
    /// spelled, or by where that path leads once the junctions and symbolic links in it are followed.
    /// </summary>
    /// <param name="real">What a binding has already found out about where its folders are; none, and each is asked again.</param>
    internal static bool IsTheSource(string folder, Source source, RealPaths? real = null)
    {
        var spelled = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.RootPath));
        return string.Equals(spelled, sourceRoot, RealPaths.Comparison) || (real ?? new RealPaths()).Same(spelled, sourceRoot);
    }

    /// <summary>A binding that finds nothing, and says why: the only layout left would read the protected folder itself.</summary>
    internal static SourceBinding Unusable(Destination destination, Sample sample, MirrorLayout layout, string basePath) =>
        new(destination, sample.Source, layout, basePath, 0, Math.Min(ProbeFiles, sample.Files.Count))
        {
            Problem = Language.Pick(
                $"the copies would be looked for in {sample.Source.RootPath}, the protected folder itself",
                $"사본을 찾을 곳이 보호하는 폴더 {sample.Source.RootPath} 자체입니다"),
            NothingThere = true,
        };

    /// <summary>Why no copy was found, when a folder that would have been looked in is the protected folder itself.</summary>
    private static string NotACopy(string folder) => Language.Pick(
        $"{folder} is the protected folder itself, not a copy of it",
        $"{Korean.Topic(folder)} 사본이 아니라 보호하는 폴더 자체입니다");

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

    public Task<CopyLookup> LocateAsync(SourceBinding binding, string relativePath, CancellationToken ct)
    {
        // Never the protected folder itself, and never a "relative" path that leads out of the backup (a rooted one, or
        // one with ..), as Path.Combine would happily make of it. The backup may be the root of a drive, E:\ or /.
        var basePath = Path.GetFullPath(binding.BasePath);
        var path = Path.GetFullPath(Path.Combine(basePath, relativePath));
        if (binding.NothingThere || !SourceFiles.IsInside(basePath, path)) return Task.FromResult(CopyLookup.Missing(path, binding.Problem));

        return Task.FromResult(LocateFile(binding, relativePath, path));
    }

    private static CopyLookup LocateFile(SourceBinding binding, string relativePath, string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return CopyLookup.Missing(path);

            // Read back, the original would be compared with itself and pass: there is no separate copy to read.
            if (IsTheOriginal(binding, relativePath, info))
            {
                return CopyLookup.Missing(path, Language.Pick(
                    $"{path} is the original itself, reached through a link, not a copy of it",
                    $"{path}에 있는 파일은 사본이 아니라 링크로 연결된 원본 자체입니다"));
            }

            if (CloudFiles.IsNotHere(info.Attributes))
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

    /// <summary>
    /// Whether what was found is the original file itself, reached through a junction or a symbolic link below the
    /// backup folder: a "Documents" in the backup that is a link to the one being protected, say. Binding cannot see
    /// those, so each copy is asked here, where a drill, a rehearsal and a scan all come to find it. A folder is
    /// resolved once for all the files in it. The original is resolved the same way: sampling does not go through
    /// links, but a rehearsal of one folder starts in the folder it is given, which may be a link in the protected
    /// folder, and the backup may hold that same link.
    /// </summary>
    private static bool IsTheOriginal(SourceBinding binding, string relativePath, FileInfo copy)
    {
        var real = binding.State as RealPaths ?? new RealPaths();
        var original = real.OfFile(Path.Combine(binding.Source.RootPath, relativePath), isLink: false);
        var found = real.OfFile(copy.FullName, isLink: (copy.Attributes & FileAttributes.ReparsePoint) != 0);
        return string.Equals(found, original, RealPaths.Comparison);
    }

    public Task<ReadBack> ReadBackAsync(SourceBinding binding, CopyLookup copy, string scratchPath, CancellationToken ct) =>
        copy.State != CopyState.Found || copy.Path is null
            ? Task.FromResult(new ReadBack(false, 0, TimeSpan.Zero, null, Language.Pick("no copy to read", "읽을 사본이 없습니다")))
            : CopyReader.ReadAsync(copy.Path, scratchPath, copy.FetchTime, ct);
}
