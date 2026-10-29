using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Boatdrill.Core.Drill;
using Boatdrill.Core.Sampling;
using Boatdrill.Core.Tools;
using Boatdrill.Core.Verification;

namespace Boatdrill.Core.Adapters;

/// <summary>
/// A restic repository, read through the restic command-line tool.
/// <para>
/// For each source the newest snapshot that covers it is listed once with <c>ls</c>, so finding a copy costs
/// nothing; then the sampled files are restored with <c>restore --include</c> in a few batches, which is a real
/// restore test, timed. A file the batches cannot ask for (restic reads a name with brackets as a pattern) is
/// fetched on its own with <c>dump</c>. Every command runs with <c>--no-lock</c>, so nothing is written into the
/// repository, and the password travels in restic's environment, never on its command line.
/// </para>
/// </summary>
public sealed partial class ResticAdapter : IDestinationAdapter
{
    /// <summary>Characters of --include arguments per restore call: well inside Windows' 32,767 for a command line.</summary>
    private const int BatchCharacters = 20_000;

    /// <summary>What each --include adds besides the path: the flag, a space and quotes.</summary>
    private const int IncludeOverhead = 12;

    // Safety nets for a stalled network, not targets: a restore of the whole 512 MB sample over a slow link fits.
    private static readonly TimeSpan ListLimit = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RestoreLimit = TimeSpan.FromHours(2);
    private static readonly TimeSpan DumpLimit = TimeSpan.FromMinutes(30);

    private static readonly char[] PatternCharacters = ['*', '?', '[', ']', '\\'];

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public DestinationKind Kind => DestinationKind.Restic;

    /// <summary>restic authenticates every piece of a file as it restores it; damage shows as an error, not as other bytes.</summary>
    public bool ChecksItsOwnBytes => true;

    public async Task<DestinationProbe> ProbeAsync(Destination destination, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(destination.Secret)) return new DestinationProbe(false, "no password is saved for it", null);

        var result = await RunAsync(destination, ["snapshots", "--json"], ListLimit, ct).ConfigureAwait(false);
        if (result.ExitCode != 0) return new DestinationProbe(false, Explain(destination, result), null);

        var count = ParseSnapshots(result.Output).Count;
        return new DestinationProbe(true, $"{destination.DisplayLocation} holds {count:N0} {(count == 1 ? "snapshot" : "snapshots")}", null);
    }

    public async Task<SourceBinding> BindAsync(Destination destination, Sample sample, string scratchDirectory, BindPurpose purpose,
        IReadOnlyCollection<string> alsoRead, CancellationToken ct)
    {
        var sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sample.Source.RootPath));
        var treeRoot = TreePath(sourceRoot);
        var probe = sample.Files.Take(MirrorFolderAdapter.ProbeFiles).ToList();

        SourceBinding Unbound(string problem) => new(destination, sample.Source, MirrorLayout.FullPath, destination.DisplayLocation, 0, probe.Count)
        {
            State = new State(null, new Dictionary<string, Node>(PathComparer), string.Empty, sourceRoot),
            Problem = problem,
        };

        var listed = await RunAsync(destination, ["snapshots", "--json"], ListLimit, ct).ConfigureAwait(false);
        if (listed.ExitCode != 0) return Unbound(Explain(destination, listed));

        if (Choose(ParseSnapshots(listed.Output), treeRoot) is not { } chosen)
        {
            return Unbound($"no snapshot in the repository holds {sourceRoot}");
        }

        var (snapshot, roots) = chosen;
        var index = new Dictionary<string, Node>(PathComparer);
        var listing = await RunAsync(destination, ["ls", snapshot.Id, "--json", "--recursive", .. roots], ListLimit, ct, line: text =>
        {
            if (ParseNode(text) is { } node) index[node.Path] = node;
        }).ConfigureAwait(false);
        if (listing.ExitCode != 0) return Unbound(Explain(destination, listing));

        var state = new State(snapshot, index, Path.Combine(scratchDirectory, $"restic-{destination.Id}-{sample.Source.Id}"), sourceRoot);
        if (purpose == BindPurpose.Read)
        {
            await RestoreAsync(destination, [.. sample.Files.Select(f => f.RelativePath), .. alsoRead], state, ct).ConfigureAwait(false);
        }

        var hits = probe.Count(f => index.ContainsKey(TreePath(Path.Combine(sourceRoot, f.RelativePath))));
        return new SourceBinding(destination, sample.Source, MirrorLayout.FullPath,
            $"{destination.DisplayLocation}, snapshot {snapshot.ShortId} of {snapshot.Time.UtcDateTime:d MMMM yyyy, HH:mm} UTC", hits, probe.Count)
        {
            State = state,
        };
    }

    public Task<CopyLookup> LocateAsync(SourceBinding binding, string relativePath, CancellationToken ct)
    {
        var state = (State)binding.State!;
        var tree = TreePath(Path.Combine(state.SourceRoot, relativePath));
        if (state.Snapshot is not { } snapshot) return Task.FromResult(CopyLookup.Missing(tree, binding.Problem));

        if (!state.Index.TryGetValue(tree, out var node))
        {
            return Task.FromResult(CopyLookup.Missing(tree, $"not in snapshot {snapshot.ShortId} of {snapshot.Time.UtcDateTime:d MMMM yyyy, HH:mm} UTC"));
        }

        var staged = StagedPath(state.Stage, node.Path);
        var fetch = state.RestoredBytes > 0 && File.Exists(staged)
            ? TimeSpan.FromTicks((long)(state.RestoreTime.Ticks * ((double)node.Size / state.RestoredBytes)))
            : TimeSpan.Zero;
        return Task.FromResult(new CopyLookup(CopyState.Found, staged, node.Size, node.ModifiedUtc, null) { FetchTime = fetch, Identity = node.Path });
    }

    public async Task<ReadBack> ReadBackAsync(SourceBinding binding, CopyLookup copy, string scratchPath, CancellationToken ct)
    {
        if (copy.State != CopyState.Found || copy.Path is null) return new ReadBack(false, 0, TimeSpan.Zero, null, "no copy to read");

        var state = (State)binding.State!;
        if (File.Exists(copy.Path))
        {
            // Restored by a batch: that restore was the read-back, so hash the file where it landed, then free the space.
            var watch = Stopwatch.StartNew();
            try
            {
                var hash = await Hashing.Sha256HexAsync(copy.Path, ct).ConfigureAwait(false);
                return new ReadBack(true, new FileInfo(copy.Path).Length, watch.Elapsed + copy.FetchTime, hash, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new ReadBack(false, 0, watch.Elapsed + copy.FetchTime, null, ex.Message);
            }
            finally
            {
                Scratch.TryDeleteFile(copy.Path);
            }
        }

        var tree = TreeFromStaged(state.Stage, copy.Path);
        if (state.RestoreErrors.TryGetValue(tree, out var failed))
        {
            return new ReadBack(false, 0, copy.FetchTime, null, failed.Detail) { Damaged = failed.Damaged };
        }

        // A name the batches could not ask for: fetch this one file on its own.
        var timer = Stopwatch.StartNew();
        ToolRunner.Result result;
        await using (var file = new FileStream(scratchPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
        {
            result = await RunAsync(binding.Destination, ["dump", state.Snapshot!.Id, tree], DumpLimit, ct, output: file).ConfigureAwait(false);
        }

        timer.Stop();
        if (result.ExitCode != 0)
        {
            var damaged = IsDamage(result.Error);
            return new ReadBack(false, 0, timer.Elapsed, null, damaged ? Damage(Explain(binding.Destination, result)) : Explain(binding.Destination, result))
            {
                Damaged = damaged,
            };
        }

        var restored = await Hashing.Sha256HexAsync(scratchPath, ct).ConfigureAwait(false);
        return new ReadBack(true, new FileInfo(scratchPath).Length, timer.Elapsed, restored, null);
    }

    /// <summary>Restores the files that are in the snapshot into the stage folder, timing it.</summary>
    private static async Task RestoreAsync(Destination destination, IEnumerable<string> relativePaths, State state, CancellationToken ct)
    {
        var wanted = relativePaths
            .Select(p => state.Index.GetValueOrDefault(TreePath(Path.Combine(state.SourceRoot, p))))
            .OfType<Node>()
            .Where(n => n.Path.IndexOfAny(PatternCharacters) < 0)
            .DistinctBy(n => n.Path, PathComparer)
            .ToList();

        var watch = Stopwatch.StartNew();
        foreach (var batch in Batches(wanted))
        {
            List<string> arguments = ["restore", state.Snapshot!.Id, "--target", state.Stage];
            foreach (var node in batch)
            {
                arguments.Add("--include");
                arguments.Add(node.Path);
            }

            var result = await RunAsync(destination, arguments, RestoreLimit, ct).ConfigureAwait(false);
            if (result.ExitCode == 0) continue;

            // restic carries on past a file it cannot restore and names it, sometimes twice (the first says why);
            // anything else applies to the whole batch.
            var general = Explain(destination, result);
            var named = new Dictionary<string, string>(PathComparer);
            foreach (Match match in RestoreError().Matches(result.Error)) named.TryAdd(match.Groups["path"].Value, match.Groups["error"].Value.Trim());

            foreach (var node in batch)
            {
                if (File.Exists(StagedPath(state.Stage, node.Path))) continue;

                state.RestoreErrors[node.Path] = named.TryGetValue(node.Path, out var why)
                    ? new Failure(IsDamage(why) ? Damage("restic said: " + why) : "restic could not restore it: " + why, IsDamage(why))
                    : new Failure(general, IsDamage(general));
            }
        }

        state.RestoreTime = watch.Elapsed;
        state.RestoredBytes = wanted.Sum(n => n.Size);
    }

    // ---------------- restic ----------------

    private static Task<ToolRunner.Result> RunAsync(Destination destination, IEnumerable<string> arguments, TimeSpan limit, CancellationToken ct,
        Stream? output = null, Action<string>? line = null)
    {
        var environment = new Dictionary<string, string?>
        {
            ["RESTIC_REPOSITORY"] = destination.RootPath,
            ["RESTIC_PASSWORD"] = destination.Secret,
            // Set for the user's own use, these would win over the repository and password given here.
            ["RESTIC_REPOSITORY_FILE"] = null,
            ["RESTIC_PASSWORD_FILE"] = null,
            ["RESTIC_PASSWORD_COMMAND"] = null,
        };

        return ToolRunner.RunAsync(destination.ToolPath ?? "restic", ["--no-lock", "--quiet", .. arguments], environment, limit, ct, output, line);
    }

    /// <summary>restic's own error, in words a person can act on.</summary>
    internal static string Explain(Destination destination, ToolRunner.Result result)
    {
        if (result.NotStarted) return "restic could not be started; install it, or say where it is in Settings";
        if (result.TimedOut) return "restic stopped answering: " + result.Error;

        var error = result.Error;
        if (error.Contains("wrong password", StringComparison.OrdinalIgnoreCase)) return "the password was not accepted";
        if (error.Contains("unable to open config file", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("Is there a repository", StringComparison.OrdinalIgnoreCase))
        {
            return "no restic repository was found at " + destination.DisplayLocation;
        }

        var first = error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (first is null) return $"restic stopped with exit code {result.ExitCode}";
        return "restic said: " + (first.StartsWith("Fatal: ", StringComparison.Ordinal) ? first[7..] : first);
    }

    internal sealed record Snapshot(string Id, string ShortId, DateTimeOffset Time, IReadOnlyList<string> Paths, string Hostname);

    internal sealed record Node(string Path, long Size, DateTimeOffset ModifiedUtc);

    private sealed record State(Snapshot? Snapshot, Dictionary<string, Node> Index, string Stage, string SourceRoot)
    {
        public TimeSpan RestoreTime { get; set; }

        public long RestoredBytes { get; set; }

        /// <summary>Files a restore batch asked for and did not bring back, with why.</summary>
        public Dictionary<string, Failure> RestoreErrors { get; } = new(PathComparer);
    }

    private sealed record Failure(string Detail, bool Damaged);

    /// <summary>
    /// restic checks every piece of a file as it restores it; these are its words for a piece that fails the
    /// check. A pack it cannot open is not among them: over a network that is as likely an outage as a loss.
    /// </summary>
    internal static bool IsDamage(string error) =>
        error.Contains("ciphertext verification failed", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("invalid hash", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("hash mismatch", StringComparison.OrdinalIgnoreCase);

    private static string Damage(string said) => "the copy in the repository is damaged; " + said;

    internal static IReadOnlyList<Snapshot> ParseSnapshots(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return [];

        var snapshots = new List<Snapshot>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var i) || i.GetString() is not { Length: >= 8 } id) continue;
            if (!item.TryGetProperty("time", out var t) || ParseTime(t.GetString()) is not { } time) continue;

            var shortId = item.TryGetProperty("short_id", out var s) ? s.GetString() ?? id[..8] : id[..8];
            var paths = item.TryGetProperty("paths", out var p) && p.ValueKind == JsonValueKind.Array
                ? p.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToList()
                : [];
            var host = item.TryGetProperty("hostname", out var h) ? h.GetString() ?? string.Empty : string.Empty;
            snapshots.Add(new Snapshot(id, shortId, time, paths, host));
        }

        return snapshots;
    }

    /// <summary>A file node from one line of `restic ls --json`, or null for the snapshot line, folders and links.</summary>
    internal static Node? ParseNode(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "file") return null;
            if (!root.TryGetProperty("path", out var path) || path.GetString() is not { } p) return null;

            var size = root.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
            var modified = root.TryGetProperty("mtime", out var m) ? ParseTime(m.GetString()) : null;
            return new Node(p, size, modified ?? DateTimeOffset.MinValue);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>restic writes Go's RFC 3339 with up to nine digits of seconds; .NET keeps seven.</summary>
    internal static DateTimeOffset? ParseTime(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;

        var trimmed = LongFraction().Replace(text, m => m.Groups[1].Value);
        return DateTimeOffset.TryParse(trimmed, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var when)
            ? when.ToUniversalTime()
            : null;
    }

    /// <summary>
    /// The snapshot to drill against, and the folders to list in it: the newest snapshot that holds the whole
    /// source, else the newest that holds part of it; one taken on this PC wins over one taken elsewhere.
    /// </summary>
    internal static (Snapshot Snapshot, IReadOnlyList<string> Roots)? Choose(IReadOnlyList<Snapshot> snapshots, string treeRoot) =>
        snapshots
            .Select(s => (Snapshot: s, Cover: Coverage(s, treeRoot)))
            .Where(c => c.Cover.Rank > 0)
            .OrderByDescending(c => c.Cover.Rank)
            .ThenByDescending(c => TakenHere(c.Snapshot.Hostname))
            .ThenByDescending(c => c.Snapshot.Time)
            .Select(c => ((Snapshot, IReadOnlyList<string>)?)(c.Snapshot, c.Cover.Roots))
            .FirstOrDefault();

    /// <summary>
    /// How much of the source a snapshot holds (2 all of it, 1 part, 0 none), and the folders to list for it,
    /// spelled the way the snapshot spells them: restic keeps the case a path was typed in, and its path
    /// arguments match case exactly, even on Windows.
    /// </summary>
    internal static (int Rank, IReadOnlyList<string> Roots) Coverage(Snapshot snapshot, string treeRoot)
    {
        var parts = new List<string>();
        foreach (var path in snapshot.Paths.Select(TreePath))
        {
            var stem = path.TrimEnd('/');
            if (string.Equals(path, treeRoot, PathComparison)) return (2, [path]);
            if (treeRoot.StartsWith(stem + "/", PathComparison)) return (2, [stem + treeRoot[stem.Length..]]);
            if (path.StartsWith(treeRoot.TrimEnd('/') + "/", PathComparison)) parts.Add(path);
        }

        return (parts.Count > 0 ? 1 : 0, parts);
    }

    /// <summary>
    /// restic records Go's host name; .NET's machine name is the 15-character NetBIOS form of it and, on Linux,
    /// stops at the first dot.
    /// </summary>
    internal static bool TakenHere(string hostname)
    {
        var host = hostname.Split('.')[0];
        var here = Environment.MachineName;
        return string.Equals(host, here, StringComparison.OrdinalIgnoreCase) ||
               (here.Length == 15 && host.StartsWith(here, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>How restic names a path inside a snapshot: C:\Users\max becomes /C/Users/max; a Unix path stays as it is.</summary>
    internal static string TreePath(string fullPath)
    {
        if (OperatingSystem.IsWindows())
        {
            var root = Path.GetPathRoot(fullPath) ?? string.Empty;
            if (root.Length >= 2 && root[1] == ':')
            {
                var rest = fullPath[root.Length..].Replace('\\', '/').Trim('/');
                return rest.Length == 0 ? "/" + root[0] : $"/{root[0]}/{rest}";
            }
        }

        // Elsewhere a backslash is an ordinary character in a file name.
        return fullPath.Length > 1 ? fullPath.TrimEnd('/') : fullPath;
    }

    private static string StagedPath(string stage, string tree) =>
        Path.Combine(stage, tree.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

    private static string TreeFromStaged(string stage, string staged) =>
        "/" + Path.GetRelativePath(stage, staged).Replace(Path.DirectorySeparatorChar, '/');

    private static IEnumerable<List<Node>> Batches(IEnumerable<Node> nodes)
    {
        var batch = new List<Node>();
        var length = 0;
        foreach (var node in nodes)
        {
            if (batch.Count > 0 && length + node.Path.Length + IncludeOverhead > BatchCharacters)
            {
                yield return batch;
                batch = [];
                length = 0;
            }

            batch.Add(node);
            length += node.Path.Length + IncludeOverhead;
        }

        if (batch.Count > 0) yield return batch;
    }

    [GeneratedRegex(@"^ignoring error for (?<path>.+?): (?<error>.+)$", RegexOptions.Multiline)]
    private static partial Regex RestoreError();

    [GeneratedRegex(@"(\.\d{7})\d+")]
    private static partial Regex LongFraction();
}
