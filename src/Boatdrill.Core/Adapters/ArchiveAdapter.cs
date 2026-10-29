using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Boatdrill.Core.Drill;
using Boatdrill.Core.Sampling;
using Boatdrill.Core.Text;
using Boatdrill.Core.Tools;
using Boatdrill.Core.Verification;

namespace Boatdrill.Core.Adapters;

/// <summary>
/// Zip and 7z archives: a folder of them ("Documents 2026-10-15.zip") or one. For each source, the newest archive
/// that holds its files is its backup, and the entries are laid out as in a folder of copies.
/// <para>
/// A zip is read in-process; a 7z through 7-Zip's command line, which checks every file's CRC as it extracts it.
/// Either way a drill's sample is extracted in one pass into the scratch folder (one file at a time from a solid 7z
/// archive would unpack it over and over), and that pass is the timed read-back. Archives are only ever opened for
/// reading. A password-protected archive cannot be read yet, and says so.
/// </para>
/// </summary>
public sealed partial class ArchiveAdapter : IDestinationAdapter
{
    /// <summary>Archives looked through for each source, newest first.</summary>
    private const int ArchivesTried = 10;

    private static readonly TimeSpan ListLimit = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ExtractLimit = TimeSpan.FromHours(2);
    private static readonly TimeSpan OneLimit = TimeSpan.FromMinutes(30);

    private static string Encrypted => Language.Pick(
        "it is password-protected, and Boatdrill cannot open encrypted archives yet",
        "암호로 보호되어 있어 Boatdrill이 아직 열 수 없습니다");

    private static StringComparer NameComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly ZipNameEncoding _zipNames;

    /// <param name="zipCodePage">The code page of zip names not marked as UTF-8; by default Windows' OEM code page.</param>
    public ArchiveAdapter(int? zipCodePage = null)
    {
        _zipNames = new ZipNameEncoding(zipCodePage ?? OemCodePage());
    }

    public DestinationKind Kind => DestinationKind.Archive;

    public Task<DestinationProbe> ProbeAsync(Destination destination, CancellationToken ct)
    {
        var archives = Archives(destination.RootPath);
        if (archives is null) return Task.FromResult(new DestinationProbe(false, NotFound(destination.RootPath), null));
        if (archives.Count == 0) return Task.FromResult(new DestinationProbe(false, NoArchives(destination.RootPath), null));

        var free = MirrorFolderAdapter.ProbeFolder(Path.GetDirectoryName(archives[0].FullName)!).FreeBytes;
        var newest = Language.Date(new DateTimeOffset(archives[0].LastWriteTimeUtc, TimeSpan.Zero));
        var detail = (archives.Count, Language.IsKorean) switch
        {
            (1, false) => $"{archives[0].Name} is there, from {newest}",
            (1, true) => $"{newest}에 만든 {archives[0].Name} 파일이 있습니다",
            (_, false) => $"{destination.RootPath} holds {archives.Count:N0} archives, the newest from {newest}",
            (_, true) => $"{destination.RootPath}에 압축 파일이 {archives.Count:N0}개 있고, 가장 최근 것은 {newest}에 만들었습니다",
        };
        return Task.FromResult(new DestinationProbe(true, detail, free));
    }

    /// <summary>The archive itself, or the archives in a folder, newest first; null when there is nothing there.</summary>
    internal static IReadOnlyList<FileInfo>? Archives(string root)
    {
        try
        {
            if (File.Exists(root)) return Destination.IsArchiveFile(root) ? [new FileInfo(root)] : [];
            if (!Directory.Exists(root)) return null;

            return new DirectoryInfo(root).EnumerateFiles().Where(f => Destination.IsArchiveFile(f.Name)).OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task<SourceBinding> BindAsync(Destination destination, Sample sample, string scratchDirectory, BindPurpose purpose,
        IReadOnlyCollection<string> alsoRead, CancellationToken ct)
    {
        var probe = sample.Files.Take(MirrorFolderAdapter.ProbeFiles).Select(f => Normalise(f.RelativePath)).ToList();
        var prefixes = Prefixes(sample.Source).Where(p => destination.Layout == MirrorLayout.Auto || p.Layout == destination.Layout).ToList();
        var stage = Path.Combine(scratchDirectory, $"archive-{destination.Id}-{sample.Source.Id}");

        // The newest archive that holds the most of a few sampled files, under whichever layout finds them.
        State? best = null;
        var unreadable = new List<string>();
        foreach (var archive in Archives(destination.RootPath)?.Take(ArchivesTried) ?? [])
        {
            var (entries, problem) = await ListAsync(destination, archive.FullName, ct).ConfigureAwait(false);
            if (entries is null)
            {
                unreadable.Add($"{archive.Name}: {problem}");
                continue;
            }

            foreach (var (layout, prefix) in prefixes)
            {
                var hits = probe.Count(p => entries.ContainsKey(prefix + p));
                if (best is null || hits > best.Hits) best = new State(archive.FullName, entries, layout, prefix, hits, stage);
            }

            if (best is not null && best.Hits == probe.Count && probe.Count > 0) break;
        }

        if (best is null)
        {
            var empty = new State(destination.RootPath, new Dictionary<string, Entry>(NameComparer), MirrorLayout.Contents, string.Empty, 0, stage);
            return new SourceBinding(destination, sample.Source, MirrorLayout.Contents, destination.RootPath, 0, probe.Count)
            {
                State = empty,
                Problem = unreadable.Count > 0
                    ? Language.Pick("no archive could be read (", "읽을 수 있는 압축 파일이 없습니다(") + string.Join("; ", unreadable) + ")"
                    : NoArchives(destination.RootPath),
            };
        }

        if (purpose == BindPurpose.Read)
        {
            await ExtractAsync(destination, [.. sample.Files.Select(f => f.RelativePath), .. alsoRead], best, ct).ConfigureAwait(false);
        }

        // Inside an archive as Explorer shows it: D:\Backups\Laptop.zip\Documents.
        var where = Path.Combine(best.ArchivePath, best.Prefix.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar));
        return new SourceBinding(destination, sample.Source, best.Layout, where, best.Hits, probe.Count) { State = best };
    }

    /// <summary>Extracts the next batch of a rehearsal into the stage folder, as binding does for a drill's sample.</summary>
    public Task PrefetchAsync(SourceBinding binding, IReadOnlyCollection<string> relativePaths, CancellationToken ct) =>
        binding.State is State { Entries.Count: > 0 } state ? ExtractAsync(binding.Destination, relativePaths, state, ct) : Task.CompletedTask;

    public Task<CopyLookup> LocateAsync(SourceBinding binding, string relativePath, CancellationToken ct)
    {
        var state = (State)binding.State!;
        var key = state.Prefix + Normalise(relativePath);
        if (!state.Entries.TryGetValue(key, out var entry))
        {
            var archiveName = Path.GetFileName(state.ArchivePath);
            return Task.FromResult(CopyLookup.Missing(state.ArchivePath + "!" + key, binding.Problem ?? Language.Pick("not in " + archiveName, archiveName + "에 없습니다")));
        }

        var staged = StagedPath(state.Stage, key);
        var fetch = state.FetchedBytes > 0 && File.Exists(staged)
            ? TimeSpan.FromTicks((long)(state.FetchTime.Ticks * ((double)entry.Size / state.FetchedBytes)))
            : TimeSpan.Zero;
        return Task.FromResult(new CopyLookup(CopyState.Found, staged, entry.Size, entry.ModifiedUtc, null)
        {
            FetchTime = fetch,
            Identity = state.ArchivePath + "!" + entry.Name,
        });
    }

    public async Task<ReadBack> ReadBackAsync(SourceBinding binding, CopyLookup copy, string scratchPath, CancellationToken ct)
    {
        if (copy.State != CopyState.Found || copy.Path is null) return new ReadBack(false, 0, TimeSpan.Zero, null, Language.Pick("no copy to read", "읽을 사본이 없습니다"));

        var state = (State)binding.State!;
        var watch = Stopwatch.StartNew();
        if (File.Exists(copy.Path))
        {
            // Extracted with the batch, which was the read-back: move the file into place and hash it there.
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

        var key = Path.GetRelativePath(state.Stage, copy.Path).Replace(Path.DirectorySeparatorChar, '/');
        if (state.Failures.TryGetValue(key, out var failed))
        {
            return new ReadBack(false, 0, copy.FetchTime, null, failed.Detail) { Damaged = failed.Damaged };
        }

        // Not extracted with a batch: this one file on its own.
        var entry = state.Entries[key];
        var single = IsZip(state.ArchivePath)
            ? ExtractZip(state.ArchivePath, [(entry, scratchPath)])
            : await ExtractSevenZipOneAsync(binding.Destination, state.ArchivePath, entry, scratchPath, ct).ConfigureAwait(false);
        watch.Stop();
        if (single.FirstOrDefault() is { } failure) return new ReadBack(false, 0, watch.Elapsed, null, failure.Value.Detail) { Damaged = failure.Value.Damaged };

        var restored = await Hashing.Sha256HexAsync(scratchPath, ct).ConfigureAwait(false);
        return new ReadBack(true, new FileInfo(scratchPath).Length, watch.Elapsed, restored, null);
    }

    // ---------------- listing ----------------

    /// <summary>An archive's files by name, laid out with forward slashes; null and why when it cannot be read.</summary>
    private async Task<(Dictionary<string, Entry>? Entries, string? Problem)> ListAsync(Destination destination, string archive, CancellationToken ct)
    {
        var entries = new Dictionary<string, Entry>(NameComparer);
        if (IsZip(archive))
        {
            try
            {
                using var zip = OpenZip(archive);
                foreach (var entry in zip.Entries)
                {
                    if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
                    if (entry.IsEncrypted) return (null, Encrypted);
                    entries[Normalise(entry.FullName)] = new Entry(entry.FullName, entry.Length, entry.LastWriteTime.ToUniversalTime());
                }

                return (entries, null);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                return (null, CouldNotOpenZip + ex.Message);
            }
        }

        var result = await SevenZipAsync(destination, ["l", "-slt", "-ba", archive], ListLimit, ct).ConfigureAwait(false);
        if (result.ExitCode != 0) return (null, IsPasswordError(result.Error + result.Output) ? Encrypted : ExplainSevenZip(result));

        foreach (var entry in ParseSevenZipListing(result.Output))
        {
            if (entry.Encrypted) return (null, Encrypted);
            entries[Normalise(entry.Name)] = entry;
        }

        return (entries, null);
    }

    /// <summary>Files in `7z l -slt -ba` output: blocks of "Key = Value" lines, one per entry; folders are left out.</summary>
    internal static IReadOnlyList<Entry> ParseSevenZipListing(string text)
    {
        var entries = new List<Entry>();
        foreach (var block in text.ReplaceLineEndings("\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = block.Split('\n')
                .Select(line => line.Split(" = ", 2))
                .Where(pair => pair.Length == 2)
                .GroupBy(pair => pair[0].Trim())
                .ToDictionary(g => g.Key, g => g.First()[1].Trim());
            if (!fields.TryGetValue("Path", out var path) || path.Length == 0) continue;
            if (fields.TryGetValue("Attributes", out var attributes) && attributes.StartsWith('D')) continue;
            if (fields.TryGetValue("Folder", out var folder) && folder == "+") continue;

            var size = fields.TryGetValue("Size", out var s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
            DateTimeOffset? modified = fields.TryGetValue("Modified", out var m) &&
                                       DateTime.TryParse(m, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local)
                ? new DateTimeOffset(local).ToUniversalTime()
                : null;
            entries.Add(new Entry(path, size, modified, fields.TryGetValue("Encrypted", out var encrypted) && encrypted == "+"));
        }

        return entries;
    }

    // ---------------- extracting ----------------

    /// <summary>Extracts the files the archive holds into the stage folder, in one pass, timing it.</summary>
    private async Task ExtractAsync(Destination destination, IEnumerable<string> relativePaths, State state, CancellationToken ct)
    {
        var wanted = relativePaths
            .Select(p => state.Prefix + Normalise(p))
            .Where(state.Entries.ContainsKey)
            .Distinct(NameComparer)
            .ToList();
        if (wanted.Count == 0) return;

        var watch = Stopwatch.StartNew();
        IReadOnlyDictionary<string, Failure> failures;
        if (IsZip(state.ArchivePath))
        {
            failures = await Task.Run(() => ExtractZip(state.ArchivePath, wanted.Select(k => (state.Entries[k], StagedPath(state.Stage, k))).ToList()), ct)
                .ConfigureAwait(false);
        }
        else
        {
            Directory.CreateDirectory(state.Stage);
            var list = state.Stage + ".txt";
            await File.WriteAllLinesAsync(list, wanted.Select(k => state.Entries[k].Name), ct).ConfigureAwait(false);
            var result = await SevenZipAsync(destination, ["x", "-y", "-bd", "-bso0", "-bsp0", "-scsUTF-8", "-o" + state.Stage, state.ArchivePath, "@" + list],
                ExtractLimit, ct).ConfigureAwait(false);
            failures = SevenZipFailures(result, wanted.Select(k => state.Entries[k]).ToList());
        }

        state.FetchTime = watch.Elapsed;
        state.FetchedBytes = wanted.Sum(k => state.Entries[k].Size);
        foreach (var (name, failure) in failures)
        {
            var key = Normalise(name);
            state.Failures[key] = failure;
            Scratch.TryDeleteFile(StagedPath(state.Stage, key));
        }
    }

    /// <summary>Extracts zip entries to the given paths; what failed, by entry name, with why.</summary>
    private IReadOnlyDictionary<string, Failure> ExtractZip(string archive, IReadOnlyList<(Entry Entry, string Path)> files)
    {
        var failures = new Dictionary<string, Failure>(NameComparer);
        try
        {
            using var zip = OpenZip(archive);
            foreach (var (entry, path) in files)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    zip.GetEntry(entry.Name)!.ExtractToFile(path, overwrite: true);
                }
                catch (InvalidDataException ex)
                {
                    failures[entry.Name] = new Failure(DamagedInArchive + ex.Message, Damaged: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    failures[entry.Name] = new Failure(Language.Pick("it could not be extracted: ", "압축을 풀 수 없습니다: ") + ex.Message, Damaged: false);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            foreach (var (entry, _) in files) failures.TryAdd(entry.Name, new Failure(CouldNotOpenZip + ex.Message, ex is InvalidDataException));
        }

        return failures;
    }

    private static async Task<IReadOnlyDictionary<string, Failure>> ExtractSevenZipOneAsync(Destination destination, string archive, Entry entry, string path,
        CancellationToken ct)
    {
        ToolRunner.Result result;
        await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
        {
            result = await SevenZipAsync(destination, ["e", "-so", "-bd", archive, entry.Name], OneLimit, ct, output: file).ConfigureAwait(false);
        }

        return SevenZipFailures(result, [entry]);
    }

    /// <summary>
    /// What 7-Zip could not extract: a file it names with a CRC or data error is damaged (and whatever it wrote is no
    /// copy); any other failure applies to every file asked for.
    /// </summary>
    private static IReadOnlyDictionary<string, Failure> SevenZipFailures(ToolRunner.Result result, IReadOnlyList<Entry> asked)
    {
        var failures = new Dictionary<string, Failure>(NameComparer);
        if (result.ExitCode == 0) return failures;

        foreach (Match match in SevenZipError().Matches(result.Error + "\n" + result.Output))
        {
            // "Data Error in encrypted file. Wrong password?" is a password, not damage.
            failures.TryAdd(match.Groups["path"].Value.Trim(), IsPasswordError(match.Value)
                ? new Failure(Encrypted, Damaged: false)
                : new Failure(DamagedInArchive + Language.Pick($"7-Zip found a {match.Groups["what"].Value}", $"7-Zip이 {match.Groups["what"].Value} 오류를 찾았습니다"), Damaged: true));
        }

        // 7-Zip may name only the file (e for a single entry), or nothing at all.
        if (failures.Count == 0 && result.ExitCode >= 2)
        {
            var detail = ExplainSevenZip(result);
            var damaged = detail.Contains("CRC", StringComparison.OrdinalIgnoreCase) || detail.Contains("Data Error", StringComparison.OrdinalIgnoreCase);
            foreach (var entry in asked) failures[entry.Name] = new Failure(damaged ? DamagedInArchive + detail : detail, damaged);
        }

        return failures;
    }

    // ---------------- 7-Zip ----------------

    private static Task<ToolRunner.Result> SevenZipAsync(Destination destination, IEnumerable<string> arguments, TimeSpan limit, CancellationToken ct,
        Stream? output = null) =>
        // -sccUTF-8: names in UTF-8 whatever the console's code page. -p-: a password that opens nothing, so 7-Zip
        // never asks for one.
        ToolRunner.RunAsync(SevenZip(destination), ["-sccUTF-8", "-p-", .. arguments], new Dictionary<string, string?>(), limit, ct, output);

    private static bool IsPasswordError(string said) =>
        said.Contains("Wrong password", StringComparison.OrdinalIgnoreCase) || said.Contains("encrypted archive", StringComparison.OrdinalIgnoreCase);

    /// <summary>7-Zip's 7z: the one named (boatdrill-cli --7z), else where its installer puts it on Windows, else on the PATH.</summary>
    private static string SevenZip(Destination destination)
    {
        if (destination.ToolPath is { Length: > 0 } named) return named;
        if (OperatingSystem.IsWindows())
        {
            foreach (var programs in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                var installed = Path.Combine(Environment.GetFolderPath(programs), "7-Zip", "7z.exe");
                if (File.Exists(installed)) return installed;
            }
        }

        return "7z";
    }

    private static string ExplainSevenZip(ToolRunner.Result result)
    {
        var korean = Language.IsKorean;
        if (result.NotStarted)
        {
            return korean ? "7-Zip을 실행할 수 없습니다. 7z 압축 파일을 읽으려면 7-zip.org에서 설치하세요" : "7-Zip could not be started; install it from 7-zip.org to read 7z archives";
        }

        if (result.TimedOut) return Language.Pick("7-Zip stopped answering: ", "7-Zip이 응답하지 않습니다: ") + result.Error;

        var said = (result.Error + "\n" + result.Output).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) || l.Contains("Can not open", StringComparison.OrdinalIgnoreCase));
        if (said is null)
        {
            return korean ? $"7-Zip이 종료 코드 {Korean.Toward(result.ExitCode.ToString(CultureInfo.InvariantCulture))} 멈췄습니다" : $"7-Zip stopped with exit code {result.ExitCode}";
        }

        return Language.Pick("7-Zip said: ", "7-Zip 메시지: ") + (said.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase) ? said[6..].Trim() : said);
    }

    private static string NotFound(string location) => Language.Pick($"{location} was not found", $"{Korean.Object(location)} 찾을 수 없습니다");

    private static string NoArchives(string location) => Language.Pick($"{location} holds no zip or 7z archives", $"{location}에 zip이나 7z 압축 파일이 없습니다");

    private static string CouldNotOpenZip => Language.Pick("the zip could not be opened: ", "zip 파일을 열 수 없습니다: ");

    private static string DamagedInArchive => Language.Pick("the copy in the archive is damaged: ", "압축 파일 안의 사본이 손상되었습니다: ");

    // ---------------- layout ----------------

    /// <summary>
    /// Where a source's files may sit inside an archive: at the top, in a folder named after the source, or under
    /// its full path, with or without the drive ("C/Users/max/Documents", "C:/Users/...", "Users/max/Documents").
    /// </summary>
    internal static IEnumerable<(MirrorLayout Layout, string Prefix)> Prefixes(Source source)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.RootPath));
        var below = Normalise(MirrorFolderAdapter.PathBelowVolume(root));
        var volume = MirrorFolderAdapter.VolumeFolder(root);
        yield return (MirrorLayout.Contents, string.Empty);
        yield return (MirrorLayout.RootFolder, Source.DisplayName(root) + "/");
        yield return (MirrorLayout.FullPath, $"{volume}/{below}/");
        if (OperatingSystem.IsWindows()) yield return (MirrorLayout.FullPath, $"{volume}:/{below}/");
        yield return (MirrorLayout.FullPath, below + "/");
    }

    /// <summary>An entry name as a key: forward slashes, no leading "./" or "/".</summary>
    private static string Normalise(string name)
    {
        var normalised = name.Replace('\\', '/');
        while (normalised.StartsWith("./", StringComparison.Ordinal)) normalised = normalised[2..];
        return normalised.TrimStart('/');
    }

    private static bool IsZip(string path) => Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase);

    private ZipArchive OpenZip(string path) => ZipFile.Open(path, ZipArchiveMode.Read, _zipNames);

    /// <summary>The code page Windows' own zip and 7-Zip write names in: the system's OEM code page, whatever the culture.</summary>
    private static int OemCodePage() => OperatingSystem.IsWindows() ? (int)GetOEMCP() : CultureInfo.CurrentCulture.TextInfo.OEMCodePage;

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    private static string StagedPath(string stage, string key) => Path.Combine(stage, key.Replace('/', Path.DirectorySeparatorChar));

    internal sealed record Entry(string Name, long Size, DateTimeOffset? ModifiedUtc, bool Encrypted = false);

    private readonly record struct Failure(string Detail, bool Damaged);

    private sealed record State(string ArchivePath, Dictionary<string, Entry> Entries, MirrorLayout Layout, string Prefix, int Hits, string Stage)
    {
        public TimeSpan FetchTime { get; set; }

        public long FetchedBytes { get; set; }

        /// <summary>Files a batch could not extract, by key, with why.</summary>
        public Dictionary<string, Failure> Failures { get; } = new(NameComparer);
    }

    /// <summary>
    /// How a zip's names are read when it does not mark them as UTF-8: as UTF-8 when they are valid UTF-8 (as macOS
    /// and most tools write them), else in the OEM code page that Windows' own zip and 7-Zip write (949 on Korean
    /// Windows, 437 on English). Read as UTF-8 regardless, "문서" written in code page 949 comes back as "����".
    /// </summary>
    internal sealed class ZipNameEncoding : Encoding
    {
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        private readonly Encoding _local;

        public ZipNameEncoding(int codePage)
        {
            RegisterProvider(CodePagesEncodingProvider.Instance);
            try
            {
                _local = GetEncoding(codePage);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                _local = Latin1;
            }
        }

        public override string GetString(byte[] bytes, int index, int count) => Pick(bytes, index, count).GetString(bytes, index, count);

        public override int GetCharCount(byte[] bytes, int index, int count) => Pick(bytes, index, count).GetCharCount(bytes, index, count);

        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex) =>
            Pick(bytes, byteIndex, byteCount).GetChars(bytes, byteIndex, byteCount, chars, charIndex);

        public override int GetMaxCharCount(int byteCount) => Math.Max(UTF8.GetMaxCharCount(byteCount), _local.GetMaxCharCount(byteCount));

        // Names are only ever read here; written, they would be UTF-8.
        public override int GetByteCount(char[] chars, int index, int count) => UTF8.GetByteCount(chars, index, count);

        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex) =>
            UTF8.GetBytes(chars, charIndex, charCount, bytes, byteIndex);

        public override int GetMaxByteCount(int charCount) => UTF8.GetMaxByteCount(charCount);

        private Encoding Pick(byte[] bytes, int index, int count)
        {
            try
            {
                StrictUtf8.GetCharCount(bytes, index, count);
                return UTF8;
            }
            catch (DecoderFallbackException)
            {
                return _local;
            }
        }
    }

    [GeneratedRegex(@"ERROR: (?<what>CRC Failed|Data Error|Headers Error|Unsupported Method)[^:]* : (?<path>.+)$", RegexOptions.Multiline)]
    private static partial Regex SevenZipError();
}
