using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Boatdrill.Core.Drill;
using Boatdrill.Core.Sampling;
using Boatdrill.Core.Text;
using Boatdrill.Core.Tools;
using Boatdrill.Core.Verification;

namespace Boatdrill.Core.Adapters;

/// <summary>
/// An rclone remote, read through rclone itself: Google Drive, OneDrive, S3, B2 and every other back end rclone
/// has a remote for in the user's own rclone config.
/// <para>
/// Copies are laid out as in a folder of copies (the remote holds the folder's contents, a folder named after it,
/// or the full path), and the layout is worked out from a few files looked up by name. A drill then looks up its
/// sample the same way and fetches it with one <c>rclone copy</c>, which is the real download test, timed; a file
/// that copy did not bring is fetched alone with <c>rclone cat</c>. rclone only ever reads from the remote here:
/// every command lists or downloads, and the one folder written to is Boatdrill's scratch folder.
/// </para>
/// </summary>
public sealed partial class RcloneAdapter : IDestinationAdapter
{
    // Safety nets for a stalled network, not targets.
    private static readonly TimeSpan ListLimit = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan CopyLimit = TimeSpan.FromHours(2);
    private static readonly TimeSpan CatLimit = TimeSpan.FromMinutes(30);

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public DestinationKind Kind => DestinationKind.Rclone;

    public async Task<DestinationProbe> ProbeAsync(Destination destination, CancellationToken ct)
    {
        var result = await RunAsync(destination, ["lsjson", "--max-depth", "1", "--no-mimetype", "--no-modtime", destination.RootPath], ListLimit, ct)
            .ConfigureAwait(false);
        if (result.ExitCode != 0) return new DestinationProbe(false, Explain(destination, destination.RootPath, result), null);

        var count = ParseListing(result.Output).Count;
        return new DestinationProbe(true, Language.Pick(
            $"{destination.DisplayLocation} holds {count:N0} {(count == 1 ? "item" : "items")} at its top",
            $"{destination.DisplayLocation} 맨 위에 항목이 {count:N0}개 있습니다"), null);
    }

    public async Task<SourceBinding> BindAsync(Destination destination, Sample sample, string scratchDirectory, BindPurpose purpose,
        IReadOnlyCollection<string> alsoRead, CancellationToken ct)
    {
        Directory.CreateDirectory(scratchDirectory);
        var list = Path.Combine(scratchDirectory, $"rclone-{destination.Id}-{sample.Source.Id}.txt");
        var probe = sample.Files.Take(MirrorFolderAdapter.ProbeFiles).Select(f => Remote(f.RelativePath)).ToList();

        // The layout: the candidate where most of a few sampled files are found, by name, without walking the remote.
        var candidates = Candidates(destination, sample.Source).Where(c => destination.Layout == MirrorLayout.Auto || c.Layout == destination.Layout).ToList();
        (MirrorLayout Layout, string Base, int Hits) best = (candidates[0].Layout, candidates[0].Base, 0);
        ToolRunner.Result? failedLayout = null;
        foreach (var (layout, @base) in candidates)
        {
            if (candidates.Count == 1 || probe.Count == 0) break;
            var (found, failed) = await LookUpAsync(destination, @base, probe, list, ct).ConfigureAwait(false);
            failedLayout ??= failed;
            if (found.Count > best.Hits) best = (layout, @base, found.Count);
        }

        Dictionary<string, Entry> index;
        ToolRunner.Result? failure;
        if (purpose == BindPurpose.ListOnly)
        {
            // A scan looks up every file, so the whole of the remote's copy is listed once.
            var listing = await RunAsync(destination, ["lsjson", "-R", "--files-only", "--no-mimetype", best.Base], ListLimit, ct).ConfigureAwait(false);
            (index, failure) = listing.ExitCode == 0
                ? (Index(ParseListing(listing.Output)), null)
                : (new Dictionary<string, Entry>(PathComparer), IsDirectoryNotFound(listing) ? null : listing);
        }
        else
        {
            (index, failure) = await LookUpAsync(destination, best.Base, [.. sample.Files.Select(f => Remote(f.RelativePath)), .. alsoRead.Select(Remote)], list, ct)
                .ConfigureAwait(false);
        }

        var state = new State(best.Base, index, Path.Combine(scratchDirectory, $"rclone-{destination.Id}-{sample.Source.Id}"), list);
        if (purpose == BindPurpose.Read)
        {
            await CopyAsync(destination, [.. sample.Files.Select(f => f.RelativePath), .. alsoRead], state, ct).ConfigureAwait(false);
        }

        // A listing that failed is not a remote that holds nothing: an expired token, a timeout, one folder it may not read.
        var hits = probe.Count(index.ContainsKey);
        var unknown = failure ?? (hits == 0 ? failedLayout : null);
        return new SourceBinding(destination, sample.Source, best.Layout, Destination.Redact(best.Base), hits, probe.Count)
        {
            State = state,
            Problem = unknown is null ? null : Language.Pick("its files could not be listed: ", "파일 목록을 가져오지 못했습니다: ") + Explain(destination, best.Base, unknown),
            CouldNotLook = unknown is not null,
        };
    }

    /// <summary>rclone's exit code for "directory not found": a layout that is not on the remote, so nothing to find there.</summary>
    private const int DirectoryNotFound = 3;

    private static bool IsDirectoryNotFound(ToolRunner.Result result) =>
        result.ExitCode == DirectoryNotFound || result.Error.Contains("directory not found", StringComparison.OrdinalIgnoreCase);

    /// <summary>Downloads the next batch of a rehearsal into the stage folder, as binding does for a drill's sample.</summary>
    public Task PrefetchAsync(SourceBinding binding, IReadOnlyCollection<string> relativePaths, CancellationToken ct) =>
        binding.State is State state ? CopyAsync(binding.Destination, relativePaths, state, ct) : Task.CompletedTask;

    public Task<CopyLookup> LocateAsync(SourceBinding binding, string relativePath, CancellationToken ct)
    {
        var state = (State)binding.State!;
        var remote = Remote(relativePath);
        var where = Join(state.Base, remote);

        // An unlisted file is not known to be missing when the listing itself failed.
        if (binding.CouldNotLook) return Task.FromResult(CopyLookup.Failed(where, binding.Problem ?? Language.Pick("its files could not be listed", "파일 목록을 가져오지 못했습니다")));
        if (!state.Index.TryGetValue(remote, out var entry))
        {
            return Task.FromResult(CopyLookup.Missing(where, Language.Pick("not on " + Destination.Redact(state.Base), $"{Destination.Redact(state.Base)}에 없습니다")));
        }

        var staged = StagedPath(state.Stage, entry.Path);
        var fetch = state.FetchedBytes > 0 && File.Exists(staged)
            ? TimeSpan.FromTicks((long)(state.FetchTime.Ticks * ((double)entry.Size / state.FetchedBytes)))
            : TimeSpan.Zero;
        return Task.FromResult(new CopyLookup(CopyState.Found, staged, entry.Size, entry.ModifiedUtc, null) { FetchTime = fetch, Identity = where });
    }

    public async Task<ReadBack> ReadBackAsync(SourceBinding binding, CopyLookup copy, string scratchPath, CancellationToken ct)
    {
        if (copy.State != CopyState.Found || copy.Path is null) return new ReadBack(false, 0, TimeSpan.Zero, null, Language.Pick("no copy to read", "읽을 사본이 없습니다"));

        var state = (State)binding.State!;
        if (File.Exists(copy.Path))
        {
            // Downloaded by a copy, which was the read-back: move the file into place and hash it there.
            var watch = Stopwatch.StartNew();
            try
            {
                File.SetAttributes(copy.Path, FileAttributes.Normal);
                File.Move(copy.Path, scratchPath, overwrite: true);
                var hash = await Hashing.Sha256HexAsync(scratchPath, ct).ConfigureAwait(false);
                return new ReadBack(true, new FileInfo(scratchPath).Length, watch.Elapsed + copy.FetchTime, hash, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new ReadBack(false, 0, watch.Elapsed + copy.FetchTime, null, ex.Message);
            }
        }

        var remote = Path.GetRelativePath(state.Stage, copy.Path).Replace(Path.DirectorySeparatorChar, '/');
        if (state.CopyErrors.TryGetValue(remote, out var failed)) return new ReadBack(false, 0, copy.FetchTime, null, failed);

        // Not brought by a copy: fetch this one file on its own.
        var timer = Stopwatch.StartNew();
        ToolRunner.Result result;
        await using (var file = new FileStream(scratchPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
        {
            result = await RunAsync(binding.Destination, ["cat", Join(state.Base, remote)], CatLimit, ct, output: file).ConfigureAwait(false);
        }

        timer.Stop();
        if (result.ExitCode != 0) return new ReadBack(false, 0, timer.Elapsed, null, Explain(binding.Destination, Join(state.Base, remote), result));

        var restored = await Hashing.Sha256HexAsync(scratchPath, ct).ConfigureAwait(false);
        return new ReadBack(true, new FileInfo(scratchPath).Length, timer.Elapsed, restored, null);
    }

    /// <summary>
    /// Which of these files (paths below <paramref name="base"/>) the remote holds, looked up by name; with the listing that
    /// failed, when one did for another reason than the folder not being there.
    /// </summary>
    private static async Task<(Dictionary<string, Entry> Index, ToolRunner.Result? Failure)> LookUpAsync(Destination destination, string @base,
        IReadOnlyCollection<string> paths, string list, CancellationToken ct)
    {
        if (paths.Count == 0) return (new Dictionary<string, Entry>(PathComparer), null);

        // --files-from-raw takes every line as a name: no comments, no patterns, so "#notes" and "[draft]" are files.
        await File.WriteAllLinesAsync(list, paths.Distinct(PathComparer), ct).ConfigureAwait(false);
        var result = await RunAsync(destination, ["lsjson", "-R", "--files-only", "--no-mimetype", "--files-from-raw", list, @base], ListLimit, ct)
            .ConfigureAwait(false);
        if (result.ExitCode == 0) return (Index(ParseListing(result.Output)), null);

        // A layout that does not exist on the remote is "directory not found": nothing found there, not a failure.
        return (new Dictionary<string, Entry>(PathComparer), IsDirectoryNotFound(result) ? null : result);
    }

    /// <summary>Downloads the files that the remote holds into the stage folder, in one rclone copy, timing it.</summary>
    private static async Task CopyAsync(Destination destination, IEnumerable<string> relativePaths, State state, CancellationToken ct)
    {
        var wanted = relativePaths.Select(Remote).Where(state.Index.ContainsKey).Distinct(PathComparer).ToList();
        if (wanted.Count == 0) return;

        await File.WriteAllLinesAsync(state.List, wanted, ct).ConfigureAwait(false);
        var watch = Stopwatch.StartNew();
        var result = await RunAsync(destination, ["copy", "--files-from-raw", state.List, state.Base, state.Stage], CopyLimit, ct).ConfigureAwait(false);
        state.FetchTime = watch.Elapsed;
        state.FetchedBytes = wanted.Sum(p => state.Index[p].Size);
        if (result.ExitCode == 0) return;

        // rclone names each file it could not copy; anything else applies to the whole copy.
        var general = Explain(destination, state.Base, result);
        var named = new Dictionary<string, string>(PathComparer);
        foreach (Match match in CopyError().Matches(result.Error)) named.TryAdd(match.Groups["path"].Value, match.Groups["error"].Value.Trim());
        foreach (var path in wanted.Where(p => !File.Exists(StagedPath(state.Stage, p))))
        {
            state.CopyErrors[path] = named.TryGetValue(path, out var why) ? Language.Pick("rclone could not download it: ", "rclone이 내려받지 못했습니다: ") + why : general;
        }
    }

    // ---------------- rclone ----------------

    private static Task<ToolRunner.Result> RunAsync(Destination destination, IEnumerable<string> arguments, TimeSpan limit, CancellationToken ct,
        Stream? output = null)
    {
        // An encrypted config's password, when one is saved; otherwise rclone uses whatever the user's own setup provides.
        var environment = new Dictionary<string, string?>();
        if (!string.IsNullOrEmpty(destination.Secret)) environment["RCLONE_CONFIG_PASS"] = destination.Secret;

        // -q: errors only; --ask-password=false: never wait at a prompt nobody will answer.
        return ToolRunner.RunAsync(destination.ToolPath ?? "rclone", ["-q", "--ask-password=false", .. arguments], environment, limit, ct, output);
    }

    /// <summary>rclone's own error, in words a person can act on.</summary>
    internal static string Explain(Destination destination, string location, ToolRunner.Result result)
    {
        var korean = Language.IsKorean;
        if (result.NotStarted)
        {
            return korean ? "rclone을 실행할 수 없습니다. 설치하거나 설정에서 위치를 알려 주세요" : "rclone could not be started; install it, or say where it is in Settings";
        }

        if (result.TimedOut) return Language.Pick("rclone stopped answering: ", "rclone이 응답하지 않습니다: ") + result.Error;

        var error = result.Error;
        if (error.Contains("didn't find section in config file", StringComparison.OrdinalIgnoreCase))
        {
            var remote = RemoteName(destination.RootPath);
            return korean
                ? $"rclone에 이름이 \"{remote}\"인 원격 저장소가 없습니다. 먼저 rclone config로 설정하세요"
                : $"rclone has no remote named \"{remote}\"; set it up with rclone config first";
        }

        if (error.Contains("decrypt", StringComparison.OrdinalIgnoreCase) && error.Contains("config", StringComparison.OrdinalIgnoreCase))
        {
            return Language.Pick("the rclone config password was not accepted", "rclone 설정 파일의 암호가 맞지 않습니다");
        }

        if (error.Contains("directory not found", StringComparison.OrdinalIgnoreCase))
        {
            var missing = Destination.Redact(location);
            return korean ? $"{Korean.Object(missing)} 찾을 수 없습니다" : missing + " was not found";
        }

        var last = error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        if (last is null)
        {
            return korean ? $"rclone이 종료 코드 {Korean.Toward(result.ExitCode.ToString(CultureInfo.InvariantCulture))} 멈췄습니다" : $"rclone stopped with exit code {result.ExitCode}";
        }

        return Language.Pick("rclone said: ", "rclone 메시지: ") + LogPrefix().Replace(last, string.Empty);
    }

    /// <summary>"gdrive" for "gdrive:Backups"; the whole location for a local folder.</summary>
    private static string RemoteName(string location)
    {
        var colon = location.IndexOf(':');
        return colon > 1 ? location[..colon] : location;
    }

    internal sealed record Entry(string Path, long Size, DateTimeOffset? ModifiedUtc);

    private sealed record State(string Base, Dictionary<string, Entry> Index, string Stage, string List)
    {
        public TimeSpan FetchTime { get; set; }

        public long FetchedBytes { get; set; }

        /// <summary>Files a copy asked for and did not bring, with why.</summary>
        public Dictionary<string, string> CopyErrors { get; } = new(PathComparer);
    }

    /// <summary>The files in rclone's lsjson output; folders and anything unreadable are left out.</summary>
    internal static IReadOnlyList<Entry> ParseListing(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];

            var entries = new List<Entry>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("IsDir", out var dir) && dir.ValueKind == JsonValueKind.True) continue;
                if (!item.TryGetProperty("Path", out var p) || p.GetString() is not { Length: > 0 } path) continue;

                var size = item.TryGetProperty("Size", out var s) && s.TryGetInt64(out var n) ? n : 0;
                var modified = item.TryGetProperty("ModTime", out var m) ? GoTime.Parse(m.GetString()) : null;
                entries.Add(new Entry(path, size, modified));
            }

            return entries;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static Dictionary<string, Entry> Index(IEnumerable<Entry> entries)
    {
        var index = new Dictionary<string, Entry>(PathComparer);
        foreach (var entry in entries) index[entry.Path] = entry;
        return index;
    }

    /// <summary>
    /// The layouts tried, in order, as in a folder of copies: the remote path holds the source's contents, a folder
    /// named after it, or its full path below a volume folder ("C/Users/max/Documents").
    /// </summary>
    internal static IEnumerable<(MirrorLayout Layout, string Base)> Candidates(Destination destination, Source source)
    {
        var root = destination.RootPath;
        var sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.RootPath));
        yield return (MirrorLayout.Contents, root);
        yield return (MirrorLayout.RootFolder, Join(root, Source.DisplayName(sourceRoot)));
        yield return (MirrorLayout.FullPath, Join(root, Remote(Path.Combine(MirrorFolderAdapter.VolumeFolder(sourceRoot), MirrorFolderAdapter.PathBelowVolume(sourceRoot)))));
    }

    /// <summary>A path below a remote location: "gdrive:" and "Docs" give "gdrive:Docs"; "gdrive:Backup" gives "gdrive:Backup/Docs".</summary>
    internal static string Join(string location, string path) =>
        location.EndsWith(':') ? location + path : location.TrimEnd('/', '\\') + "/" + path;

    /// <summary>rclone's paths use forward slashes, whatever the platform.</summary>
    private static string Remote(string relativePath) => relativePath.Replace('\\', '/');

    private static string StagedPath(string stage, string remotePath) => Path.Combine(stage, remotePath.Replace('/', Path.DirectorySeparatorChar));

    [GeneratedRegex(@"ERROR : (?<path>.+?): Failed to copy: (?<error>.+)$", RegexOptions.Multiline)]
    private static partial Regex CopyError();

    [GeneratedRegex(@"^\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2} (ERROR|NOTICE|INFO|DEBUG) ?: ?")]
    private static partial Regex LogPrefix();
}
