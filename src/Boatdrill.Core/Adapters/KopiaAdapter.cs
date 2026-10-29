using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Boatdrill.Core.Drill;
using Boatdrill.Core.Sampling;
using Boatdrill.Core.Text;
using Boatdrill.Core.Tools;
using Boatdrill.Core.Verification;

namespace Boatdrill.Core.Adapters;

/// <summary>
/// A Kopia repository, read through the kopia command-line tool: a filesystem repository in a folder (an external
/// drive, a NAS share), or whatever repository an existing connection reaches, named by that connection's
/// configuration file (KopiaUI keeps its own as %APPDATA%\kopia\repository.config).
/// <para>
/// Boatdrill connects to the repository itself, read-only, in a folder of its own: kopia then refuses every write
/// to the repository, and the user's own connection, cache and saved password are left alone. The connection keeps
/// no cache, so every byte a drill reads comes from the repository, and the password travels in kopia's
/// environment, never on its command line. kopia's update checks, its log in the repository and its error
/// notifications are turned off for these runs.
/// </para>
/// <para>
/// kopia is only asked to open a folder that holds a repository: opening a folder as a filesystem repository writes
/// a small .shards file into it when there is none. A repository has one from the day kopia makes it; a folder
/// chosen by mistake would get one.
/// </para>
/// <para>
/// For each source the newest snapshot that holds it is listed once with <c>ls -l -r</c>, so finding a copy costs
/// nothing; each copy is then streamed out with <c>show</c>, which checks every piece against its hash as it reads.
/// </para>
/// </summary>
public sealed class KopiaAdapter : IDestinationAdapter
{
    // Safety nets for a stalled network, not targets.
    private static readonly TimeSpan ConnectLimit = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ListLimit = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ShowLimit = TimeSpan.FromMinutes(30);

    /// <summary>The width `kopia ls -l` pads an object ID to, before the name.</summary>
    private const int ObjectIdWidth = 34;

    private const string ConfigFileName = "repository.config";

    /// <summary>The file a filesystem repository keeps its format in, at its top.</summary>
    private const string FormatFile = "kopia.repository.f";

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public DestinationKind Kind => DestinationKind.Kopia;

    /// <summary>kopia checks every piece of a file against its hash as it reads it; damage shows as an error, not as other bytes.</summary>
    public bool ChecksItsOwnBytes => true;

    public async Task<DestinationProbe> ProbeAsync(Destination destination, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(destination.Secret)) return new DestinationProbe(false, Language.Pick("no password is saved for it", "저장된 암호가 없습니다"), null);
        if (NotThere(destination) is { } absent) return new DestinationProbe(false, absent, null);

        var connection = Path.Combine(Path.GetTempPath(), Scratch.Prefix + "kopia-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var connected = await ConnectAsync(destination, connection, ct).ConfigureAwait(false);
            if (connected.ExitCode != 0) return new DestinationProbe(false, Explain(destination, connected), null);

            var listed = await RunAsync(destination, connection, ["snapshot", "list", "--json", "--all"], ListLimit, ct).ConfigureAwait(false);
            if (listed.ExitCode != 0) return new DestinationProbe(false, Explain(destination, listed), null);

            var count = ParseSnapshots(listed.Output).Count;
            return new DestinationProbe(true, Language.Pick(
                $"{destination.DisplayLocation} holds {count:N0} {(count == 1 ? "snapshot" : "snapshots")}",
                $"{destination.DisplayLocation}에 스냅샷이 {count:N0}개 있습니다"), null);
        }
        finally
        {
            Scratch.TryDelete(connection);
        }
    }

    public async Task<SourceBinding> BindAsync(Destination destination, Sample sample, string scratchDirectory, BindPurpose purpose,
        IReadOnlyCollection<string> alsoRead, CancellationToken ct)
    {
        var sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sample.Source.RootPath));
        var probe = sample.Files.Take(MirrorFolderAdapter.ProbeFiles).ToList();
        var state = new State(Path.Combine(scratchDirectory, $"kopia-{destination.Id}-{sample.Source.Id}"), sourceRoot);

        SourceBinding Unbound(string problem) => new(destination, sample.Source, MirrorLayout.FullPath, destination.DisplayLocation, 0, probe.Count)
        {
            State = state,
            Problem = problem,
        };

        // The connection lives in the scratch folder, which the drill, scan or rehearsal removes when it ends.
        if (await EnsureConnectedAsync(destination, state, ct).ConfigureAwait(false) is { } refused) return Unbound(refused);

        var listed = await RunAsync(destination, state.Connection, ["snapshot", "list", "--json", "--all"], ListLimit, ct).ConfigureAwait(false);
        if (listed.ExitCode != 0) return Unbound(Explain(destination, listed));

        var picks = Choose(ParseSnapshots(listed.Output), sourceRoot);
        if (picks.Count == 0)
        {
            return Unbound(Language.Pick($"no snapshot in the repository holds {sourceRoot}", $"저장소의 어느 스냅샷에도 {Korean.Subject(sourceRoot)} 없습니다"));
        }

        foreach (var pick in picks)
        {
            var (folder, problem) = await FindFolderAsync(destination, state, pick, ct).ConfigureAwait(false);
            if (folder is null) return Unbound(problem!);

            var listing = await RunAsync(destination, state.Connection, ["ls", "-l", "-r", folder], ListLimit, ct, line: text =>
            {
                if (ParseListLine(text) is { } node) state.Index[pick.Prefix.Length == 0 ? node.Path : pick.Prefix + "/" + node.Path] = node;
            }).ConfigureAwait(false);
            if (listing.ExitCode != 0) return Unbound(Explain(destination, listing));

            state.Picks.Add(pick);
        }

        var hits = probe.Count(f => state.Index.ContainsKey(Key(f.RelativePath)));
        var described = picks.Count == 1
            ? Describe(picks[0].Snapshot)
            : Language.Pick($"snapshots of {picks.Count} folders inside it", $"안쪽 폴더 {picks.Count}개의 스냅샷");
        return new SourceBinding(destination, sample.Source, MirrorLayout.FullPath, $"{destination.DisplayLocation}, {described}", hits, probe.Count)
        {
            State = state,
        };
    }

    public Task<CopyLookup> LocateAsync(SourceBinding binding, string relativePath, CancellationToken ct)
    {
        var state = (State)binding.State!;
        var key = Key(relativePath);
        if (state.Picks.Count == 0) return Task.FromResult(CopyLookup.Missing(key, binding.Problem));

        if (!state.Index.TryGetValue(key, out var node))
        {
            var holder = state.Picks.FirstOrDefault(p => p.Prefix.Length == 0 || key.StartsWith(p.Prefix + "/", PathComparison));
            return Task.FromResult(CopyLookup.Missing(key, holder is null
                ? Language.Pick("no snapshot in the repository holds its folder", "저장소의 어느 스냅샷에도 이 파일의 폴더가 없습니다")
                : Language.Pick($"not in {Describe(holder.Snapshot)}", $"{Describe(holder.Snapshot)}에 없습니다")));
        }

        // The object ID names the copy for the hash ledger: kopia gives the same bytes the same ID, in any snapshot.
        return Task.FromResult(new CopyLookup(CopyState.Found, key, node.Size, node.ModifiedUtc, null) { Identity = node.ObjectId });
    }

    public async Task<ReadBack> ReadBackAsync(SourceBinding binding, CopyLookup copy, string scratchPath, CancellationToken ct)
    {
        if (copy.State != CopyState.Found || copy.Identity is not { } objectId)
        {
            return new ReadBack(false, 0, TimeSpan.Zero, null, Language.Pick("no copy to read", "읽을 사본이 없습니다"));
        }

        var state = (State)binding.State!;
        if (await EnsureConnectedAsync(binding.Destination, state, ct).ConfigureAwait(false) is { } refused)
        {
            return new ReadBack(false, 0, TimeSpan.Zero, null, refused);
        }

        var timer = Stopwatch.StartNew();
        ToolRunner.Result result;
        await using (var file = new FileStream(scratchPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
        {
            result = await RunAsync(binding.Destination, state.Connection, ["show", objectId], ShowLimit, ct, output: file).ConfigureAwait(false);
        }

        timer.Stop();
        if (result.ExitCode != 0)
        {
            var damaged = IsDamage(result.Error);
            var said = Explain(binding.Destination, result);
            return new ReadBack(false, 0, timer.Elapsed, null, damaged ? Damage(said) : said) { Damaged = damaged };
        }

        var hash = await Hashing.Sha256HexAsync(scratchPath, ct).ConfigureAwait(false);
        return new ReadBack(true, new FileInfo(scratchPath).Length, timer.Elapsed, hash, null);
    }

    /// <summary>Walks from the snapshot's root down to the source folder, by name, and gives the folder's object ID.</summary>
    private static async Task<(string? Folder, string? Problem)> FindFolderAsync(Destination destination, State state, Pick pick, CancellationToken ct)
    {
        var folder = pick.Snapshot.RootObjectId;
        foreach (var name in pick.Below.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var shown = await RunAsync(destination, state.Connection, ["show", folder], ListLimit, ct).ConfigureAwait(false);
            if (shown.ExitCode != 0) return (null, Explain(destination, shown));

            // The names below the snapshot's own path come from the disk; the source's spelling may differ in case.
            var entries = ParseDirectory(shown.Output).Where(e => e.IsFolder).ToList();
            var next = entries.FirstOrDefault(e => e.Name == name) ?? entries.FirstOrDefault(e => string.Equals(e.Name, name, PathComparison));
            if (next is null)
            {
                return (null, Language.Pick($"{Describe(pick.Snapshot)} does not hold {state.SourceRoot}",
                    $"{Describe(pick.Snapshot)}에 {Korean.Subject(state.SourceRoot)} 없습니다"));
            }

            folder = next.ObjectId;
        }

        return (folder, null);
    }

    // ---------------- kopia ----------------

    /// <summary>Connects, unless this binding's connection is still there; the reason when it cannot.</summary>
    private static async Task<string?> EnsureConnectedAsync(Destination destination, State state, CancellationToken ct)
    {
        if (File.Exists(Path.Combine(state.Connection, ConfigFileName))) return null;
        if (NotThere(destination) is { } absent) return absent;

        var connected = await ConnectAsync(destination, state.Connection, ct).ConfigureAwait(false);
        return connected.ExitCode == 0 ? null : Explain(destination, connected);
    }

    /// <summary>
    /// Makes Boatdrill's own connection in <paramref name="connection"/>: read-only, so kopia refuses any write to
    /// the repository, and without a cache, so every read comes from the repository.
    /// </summary>
    private static Task<ToolRunner.Result> ConnectAsync(Destination destination, string connection, CancellationToken ct)
    {
        Directory.CreateDirectory(connection);
        List<string> arguments = Destination.IsKopiaConfig(destination.RootPath)
            ? ["repository", "connect", "from-config", "--file", destination.RootPath]
            : ["repository", "connect", "filesystem", "--path", destination.RootPath];
        arguments.AddRange(["--readonly", "--content-cache-size-mb", "0"]);
        return RunAsync(destination, connection, arguments, ConnectLimit, ct);
    }

    private static Task<ToolRunner.Result> RunAsync(Destination destination, string connection, IEnumerable<string> arguments, TimeSpan limit,
        CancellationToken ct, Stream? output = null, Action<string>? line = null)
    {
        var environment = new Dictionary<string, string?>
        {
            ["KOPIA_CONFIG_PATH"] = Path.Combine(connection, ConfigFileName),
            ["KOPIA_PASSWORD"] = destination.Secret,
            ["KOPIA_LOG_DIR"] = Path.Combine(connection, "logs"),
            ["KOPIA_PERSIST_CREDENTIALS_ON_CONNECT"] = "false",
            ["KOPIA_CHECK_FOR_UPDATES"] = "false",
            ["KOPIA_DISABLE_REPOSITORY_LOG"] = "true",
            ["KOPIA_SEND_ERROR_NOTIFICATIONS"] = "never",
            ["KOPIA_DISABLE_COLOR"] = "true",
            // Set for the user's own use, this would put the connection's cache in the user's.
            ["KOPIA_CACHE_DIRECTORY"] = null,
        };

        return ToolRunner.RunAsync(Tool(destination), arguments, environment, limit, ct, output, line);
    }

    /// <summary>
    /// kopia itself: the one named in Settings, else the one on the PATH, else the copy KopiaUI installs with itself,
    /// for the user only or for everyone.
    /// </summary>
    private static string Tool(Destination destination)
    {
        if (destination.ToolPath is { Length: > 0 } named) return named;
        if (!OperatingSystem.IsWindows() || OnPath("kopia.exe")) return "kopia";

        foreach (var programs in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                 })
        {
            var bundled = Path.Combine(programs, "KopiaUI", "resources", "server", "kopia.exe");
            if (File.Exists(bundled)) return bundled;
        }

        return "kopia";
    }

    private static bool OnPath(string file) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(folder => File.Exists(Path.Combine(folder.Trim('"'), file)));

    /// <summary>
    /// The configuration file of the connection KopiaUI and the kopia command line make unless told otherwise
    /// (%APPDATA%\kopia\repository.config), when there is one: the way to a repository in the cloud.
    /// </summary>
    public static string? DefaultConnection()
    {
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "kopia", ConfigFileName);
        return File.Exists(file) ? file : null;
    }

    /// <summary>
    /// Why kopia is not to be asked at all: the repository is plainly not there (a drive that is not plugged in),
    /// the folder holds no repository, or the connection goes through a Kopia server.
    /// </summary>
    private static string? NotThere(Destination destination)
    {
        var location = destination.RootPath;
        if (!Destination.IsKopiaConfig(location)) return NoRepositoryIn(location);
        if (!File.Exists(location)) return NotFound(location);

        var (folder, server) = StorageOf(location);
        if (server) return ThroughServer;
        return folder is null ? null : NoRepositoryIn(folder);
    }

    private static string? NoRepositoryIn(string folder)
    {
        if (!Directory.Exists(folder)) return NotFound(folder);
        return File.Exists(Path.Combine(folder, FormatFile)) ? null : NoRepositoryAt(folder);
    }

    /// <summary>
    /// The folder of the filesystem repository a connection's configuration file names, if that is what it names;
    /// and whether it connects through a Kopia server instead.
    /// </summary>
    internal static (string? Folder, bool Server) StorageOf(string configFile)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configFile));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, false);
            if (root.TryGetProperty("apiServer", out var server) && server.ValueKind == JsonValueKind.Object) return (null, true);

            return root.TryGetProperty("storage", out var storage) && storage.ValueKind == JsonValueKind.Object &&
                   storage.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "filesystem" &&
                   storage.TryGetProperty("config", out var config) && config.ValueKind == JsonValueKind.Object &&
                   config.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String && path.GetString() is { Length: > 0 } folder
                ? (folder, false)
                : (null, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // kopia reads it next, and says what is wrong with it.
            return (null, false);
        }
    }

    private static string NotFound(string location) => Language.Pick($"{location} was not found", $"{Korean.Object(location)} 찾을 수 없습니다");

    private static string NoRepositoryAt(string location) => Language.Pick($"no Kopia repository was found at {location}", $"{location}에서 Kopia 저장소를 찾지 못했습니다");

    private static string ThroughServer => Language.Pick(
        "this connection goes through a Kopia server, and Boatdrill reads a repository directly; add the repository's own folder or connection",
        "이 연결은 Kopia 서버를 거치는데, Boatdrill은 저장소를 직접 읽습니다. 저장소 폴더나 저장소에 직접 연결하는 설정을 추가하세요");

    private static string Said => Language.Pick("kopia said: ", "kopia 메시지: ");

    /// <summary>"snapshot 1a2b3c4d of 29 October 2026, 10:00 UTC".</summary>
    private static string Describe(Snapshot snapshot)
    {
        var time = snapshot.StartTime.UtcDateTime;
        var id = snapshot.Id.Length > 8 ? snapshot.Id[..8] : snapshot.Id;
        return Language.IsKorean
            ? $"{time.ToString("yyyy년 M월 d일 HH:mm", CultureInfo.InvariantCulture)} UTC 스냅샷 {id}"
            : $"snapshot {id} of {time.ToString("d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture)} UTC";
    }

    /// <summary>kopia's own error, in words a person can act on.</summary>
    internal static string Explain(Destination destination, ToolRunner.Result result)
    {
        var korean = Language.IsKorean;
        if (result.NotStarted)
        {
            return korean ? "kopia를 실행할 수 없습니다. 설치하거나 설정에서 위치를 알려 주세요" : "kopia could not be started; install it, or say where it is in Settings";
        }

        if (result.TimedOut) return Language.Pick("kopia stopped answering: ", "kopia가 응답하지 않습니다: ") + result.Error;

        var error = result.Error;
        if (error.Contains("invalid repository password", StringComparison.OrdinalIgnoreCase)) return Language.Pick("the password was not accepted", "암호가 맞지 않습니다");
        if (error.Contains("repository not initialized", StringComparison.OrdinalIgnoreCase)) return NoRepositoryAt(destination.DisplayLocation);
        if (error.Contains("kopia server connections are not supported", StringComparison.OrdinalIgnoreCase)) return ThroughServer;

        var last = error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        if (last is null)
        {
            return korean ? $"kopia가 종료 코드 {Korean.Toward(result.ExitCode.ToString(CultureInfo.InvariantCulture))} 멈췄습니다" : $"kopia stopped with exit code {result.ExitCode}";
        }

        // kopia prints its error last, after any warnings; older versions put the level in front of it.
        return Said + (last.StartsWith("ERROR ", StringComparison.Ordinal) ? last[6..] : last);
    }

    /// <summary>
    /// kopia's words for a piece of a file that fails its check, or that the repository no longer has: a pack the
    /// storage says is gone is lost data, unlike a pack it could not be asked for.
    /// </summary>
    internal static bool IsDamage(string error) =>
        error.Contains("invalid checksum", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("unable to decrypt content", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("BLOB not found", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("content not found", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("object not found", StringComparison.OrdinalIgnoreCase);

    private static string Damage(string said) => Language.Pick("the copy in the repository is damaged; ", "저장소의 사본이 손상되었습니다. ") + said;

    // ---------------- what kopia prints ----------------

    /// <param name="Path">The snapshot's source folder, as kopia recorded it on the PC that took it.</param>
    /// <param name="RootObjectId">The object ID of that folder in the snapshot.</param>
    internal sealed record Snapshot(string Id, DateTimeOffset StartTime, string Host, string Path, string RootObjectId);

    /// <param name="Path">The file's path below the folder listed, names joined with '/'.</param>
    internal sealed record Node(string Path, string ObjectId, long Size, DateTimeOffset ModifiedUtc);

    /// <summary>One entry of a folder as kopia stores it.</summary>
    internal sealed record Entry(string Name, string Type, string ObjectId)
    {
        public bool IsFolder => Type == "d";
    }

    /// <summary>
    /// A snapshot to read the source from. <paramref name="Below"/> is the way from the snapshot's folder down to
    /// the source ("" when it is the source); <paramref name="Prefix"/> the way from the source down to the
    /// snapshot's folder, when the snapshot holds only that part of it.
    /// </summary>
    internal sealed record Pick(Snapshot Snapshot, string Below, string Prefix);

    private sealed class State(string connection, string sourceRoot)
    {
        /// <summary>The folder of Boatdrill's own connection to the repository, inside the scratch folder.</summary>
        public string Connection { get; } = connection;

        public string SourceRoot { get; } = sourceRoot;

        public List<Pick> Picks { get; } = [];

        /// <summary>Every file the chosen snapshots hold, by its path below the source root.</summary>
        public Dictionary<string, Node> Index { get; } = new(PathComparer);
    }

    /// <summary>The finished snapshots in the output of `kopia snapshot list --json`, of folders only.</summary>
    internal static IReadOnlyList<Snapshot> ParseSnapshots(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];

            var snapshots = new List<Snapshot>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("id", out var i) || i.GetString() is not { Length: > 0 } id) continue;
                if (!item.TryGetProperty("startTime", out var t) || GoTime.Parse(t.GetString()) is not { } started) continue;

                // A snapshot cut short (kopia saves checkpoints while it works) is not a backup to restore from.
                if (item.TryGetProperty("incomplete", out var incomplete) && incomplete.ValueKind == JsonValueKind.String && incomplete.GetString() is { Length: > 0 }) continue;

                if (!item.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Object) continue;
                if (!source.TryGetProperty("path", out var p) || p.GetString() is not { Length: > 0 } path) continue;
                var host = source.TryGetProperty("host", out var h) ? h.GetString() ?? string.Empty : string.Empty;

                if (!item.TryGetProperty("rootEntry", out var root) || root.ValueKind != JsonValueKind.Object) continue;
                if (!root.TryGetProperty("obj", out var o) || o.GetString() is not { Length: > 0 } rootObject) continue;
                if (root.TryGetProperty("type", out var type) && type.GetString() is { Length: > 0 } kind && kind != "d") continue;

                snapshots.Add(new Snapshot(id, started, host, path, rootObject));
            }

            return snapshots;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The entries of a folder object, as `kopia show` prints it: {"stream":"kopia:directory","entries":[...]}.</summary>
    internal static IReadOnlyList<Entry> ParseDirectory(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("entries", out var entries) ||
                entries.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var result = new List<Entry>();
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                if (!entry.TryGetProperty("name", out var n) || n.GetString() is not { Length: > 0 } name) continue;
                if (!entry.TryGetProperty("obj", out var o) || o.GetString() is not { Length: > 0 } objectId) continue;
                var type = entry.TryGetProperty("type", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                result.Add(new Entry(name, type, objectId));
            }

            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// A file from one line of `kopia ls -l -r`, or null for a folder, a link or anything else. kopia prints
    /// "%v %12s %v %-34v %v": the mode, the size, the local time with its zone, the object ID padded to 34
    /// characters, then the path below the folder listed, which may hold spaces of its own.
    /// </summary>
    internal static Node? ParseListLine(string line)
    {
        line = line.TrimEnd('\r');
        var fields = new (int Start, int Length)[6];
        var at = 0;
        for (var i = 0; i < fields.Length; i++)
        {
            while (at < line.Length && line[at] == ' ') at++;
            var start = at;
            while (at < line.Length && line[at] != ' ') at++;
            if (at == start) return null;
            fields[i] = (start, at - start);
        }

        string Field(int i) => line.Substring(fields[i].Start, fields[i].Length);

        var nameStart = fields[5].Start + Math.Max(ObjectIdWidth, fields[5].Length) + 1;
        if (nameStart >= line.Length || !IsFile(Field(0))) return null;
        if (!long.TryParse(Field(1), NumberStyles.None, CultureInfo.InvariantCulture, out var size)) return null;

        // Printed in this PC's own time, to the second; the zone's abbreviation adds nothing to that.
        if (!DateTime.TryParseExact($"{Field(2)} {Field(3)}", "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local))
        {
            return null;
        }

        return new Node(line[nameStart..], Field(5), size, new DateTimeOffset(local).ToUniversalTime());
    }

    /// <summary>
    /// A regular file, by Go's way of writing a mode: "-rw-r--r--", with setuid, setgid or sticky letters in front
    /// of the permissions at most; a folder starts with d, a link with L.
    /// </summary>
    private static bool IsFile(string mode) =>
        mode.Length >= 10 && mode[..^9].All(c => c is '-' or 'u' or 'g' or 't') && mode[^9..].All(c => c is 'r' or 'w' or 'x' or '-' or 's' or 'S' or 't' or 'T');

    /// <summary>
    /// The snapshots to read the source from: the newest that holds the whole source, one taken on this PC first;
    /// else the newest of each folder inside the source that has snapshots of its own.
    /// </summary>
    internal static IReadOnlyList<Pick> Choose(IReadOnlyList<Snapshot> snapshots, string sourceRoot)
    {
        var whole = snapshots
            .Select(s => (Snapshot: s, Below: Below(s.Path, sourceRoot)))
            .Where(c => c.Below is not null)
            .OrderByDescending(c => HostNames.IsThisPc(c.Snapshot.Host))
            .ThenByDescending(c => c.Snapshot.StartTime)
            .FirstOrDefault();
        if (whole.Snapshot is not null) return [new Pick(whole.Snapshot, whole.Below!, string.Empty)];

        return snapshots
            .Select(s => (Snapshot: s, Prefix: Below(sourceRoot, s.Path)))
            .Where(c => c.Prefix is { Length: > 0 })
            .GroupBy(c => c.Prefix!, PathComparer)
            .Select(g => g.OrderByDescending(c => HostNames.IsThisPc(c.Snapshot.Host)).ThenByDescending(c => c.Snapshot.StartTime).First())
            .OrderBy(c => c.Prefix, PathComparer)
            .Select(c => new Pick(c.Snapshot, string.Empty, c.Prefix!))
            .ToList();
    }

    /// <summary>
    /// The way from <paramref name="ancestor"/> down to <paramref name="path"/>, names joined with '/': empty for
    /// the folder itself, null when the path is not inside it. Case counts only where the file system's does.
    /// </summary>
    internal static string? Below(string ancestor, string path)
    {
        var top = Normalise(ancestor);
        var inner = Normalise(path);
        if (string.Equals(top, inner, PathComparison)) return string.Empty;

        var prefix = Path.EndsInDirectorySeparator(top) ? top : top + Path.DirectorySeparatorChar;
        return inner.StartsWith(prefix, PathComparison) ? inner[prefix.Length..].Replace(Path.DirectorySeparatorChar, '/') : null;
    }

    private static string Normalise(string path) =>
        Path.TrimEndingDirectorySeparator(OperatingSystem.IsWindows() ? path.Replace('/', '\\') : path);

    /// <summary>A path below the source root as the index spells it: names joined with '/'.</summary>
    private static string Key(string relativePath) => OperatingSystem.IsWindows() ? relativePath.Replace('\\', '/') : relativePath;
}
