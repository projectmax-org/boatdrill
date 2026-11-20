using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Boatdrill.Core.Adapters;
using Boatdrill.Core.Drill;
using Boatdrill.Core.Sampling;
using Boatdrill.Core.Scan;
using Boatdrill.Core.Verification;

namespace Boatdrill.Core.Tests;

/// <summary>7-Zip's 7z on the PATH (or named by BOATDRILL_7Z), for the 7z archive tests.</summary>
internal static class SevenZipTool
{
    public static string? Path { get; } = Find();

    private static string? Find()
    {
        var named = Environment.GetEnvironmentVariable("BOATDRILL_7Z");
        if (!string.IsNullOrEmpty(named)) return File.Exists(named) ? named : null;

        var candidates = new List<string>();
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            candidates.Add(System.IO.Path.Combine(folder, OperatingSystem.IsWindows() ? "7z.exe" : "7z"));
        }

        if (OperatingSystem.IsWindows()) candidates.Add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"));
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Packs everything in <paramref name="folder"/> into an archive, its contents at the top: 7z, unless the switches say otherwise.</summary>
    public static void Pack(string folder, string archive, params string[] switches)
    {
        var info = new ProcessStartInfo(Path!) { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        string[] arguments = ["a", "-bd", "-bso0", .. switches, archive, "*"];
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("7z a failed: " + error);
    }
}

/// <summary>A test that needs 7-Zip, skipped when it is not installed.</summary>
public sealed class SevenZipFactAttribute : FactAttribute
{
    public SevenZipFactAttribute()
    {
        if (SevenZipTool.Path is null) Skip = "7-Zip is not installed: put 7z on the PATH or set BOATDRILL_7Z";
    }
}

public sealed class ArchiveAdapterTests
{
    private static readonly SampleOptions Everything = new() { SampleSize = 1000, Seed = 1, ChurnWindow = TimeSpan.FromHours(-1), PerFolderFloor = 0 };

    private static readonly DateTimeOffset LastWeek = DateTimeOffset.UtcNow.AddDays(-7);

    private static Destination Archives(string location) => Destination.Archive(location, "Archives", SevenZipTool.Path);

    private static async Task<DestinationResult> DrillAsync(TempTree source, Destination destination, ArchiveAdapter? adapter = null)
    {
        using var scratch = new TempTree();
        var report = await new DrillRunner(adapter is null ? null : [adapter]).RunAsync(new DrillPlan
        {
            Sources = [Source.FromPath(source.Root, "Docs")],
            Destinations = [destination],
            ScratchDirectory = scratch.Root,
            Sampling = Everything,
        });
        TempTree.AssertEmpty(scratch.Root);
        return Assert.Single(report.Destinations);
    }

    private static void WriteDocuments(TempTree tree, int count)
    {
        for (var i = 0; i < count; i++) tree.Write($"docs/{i}.txt", $"document {i} " + new string('z', 200 * i), LastWeek);
    }

    private static string Zip(TempTree source, TempTree folder, string name, DateTimeOffset written, bool withFolder = false)
    {
        var path = folder.At(name);
        ZipFile.CreateFromDirectory(source.Root, path, CompressionLevel.Optimal, includeBaseDirectory: withFolder);
        File.SetLastWriteTimeUtc(path, written.UtcDateTime);
        return path;
    }

    [Fact]
    public async Task TheNewestZipThatHoldsTheFiles_IsTheBackup()
    {
        using var source = new TempTree();
        using var folder = new TempTree();
        using var other = new TempTree();
        WriteDocuments(source, 3);
        Zip(source, folder, "Docs 2026-10-01.zip", LastWeek.AddDays(-7));
        WriteDocuments(source, 7);
        source.Write("docs/회의록.txt", "a name the zip marks as UTF-8", LastWeek);
        Zip(source, folder, "Docs 2026-10-15.zip", LastWeek);
        other.Write("photo.jpg", "not part of the source", LastWeek);
        Zip(other, folder, "Pictures 2026-10-20.zip", LastWeek.AddDays(3));

        var result = await DrillAsync(source, Archives(folder.Root));

        Assert.True(result.Reachable, result.ProbeDetail);
        Assert.Equal(DestinationKind.Archive, result.Kind);
        Assert.Equal(8, result.Verified);
        Assert.EndsWith("Docs 2026-10-15.zip", Assert.Single(result.Bindings).BasePath);
        Assert.Contains("holds 3 archives", result.ProbeDetail);
    }

    [Fact]
    public async Task AnArchiveOfFullPaths_IsReadIntoScratch_AndNeverOverTheOriginals()
    {
        // 7z a -spf, and some zip tools, keep each file under its full path, drive and all: "C:/Users/max/Documents/a.txt".
        // Path.Combine takes such a name for a whole path, which once pointed extraction at the original file itself.
        if (!OperatingSystem.IsWindows()) return;

        using var source = new TempTree();
        using var folder = new TempTree();
        WriteDocuments(source, 4);
        var archive = folder.At("full paths.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            foreach (var file in Directory.EnumerateFiles(source.Root, "*", SearchOption.AllDirectories))
            {
                // The copy in the archive is the version before: other bytes, an earlier date.
                var entry = zip.CreateEntry(file.Replace('\\', '/'));
                entry.LastWriteTime = LastWeek.AddDays(-1);
                using var writer = new StreamWriter(entry.Open());
                writer.Write("an earlier version of " + Path.GetFileName(file));
            }
        }

        File.SetLastWriteTimeUtc(archive, LastWeek.UtcDateTime);
        var before = TreeFingerprint.Of(source.Root);

        var drill = await DrillAsync(source, Archives(folder.Root));
        using var scratch = new TempTree();
        var rehearsal = await new RehearsalRunner().RunAsync(new RehearsalPlan(Source.FromPath(source.Root, "Docs"), Archives(folder.Root), scratch.Root));

        Assert.Equal(before, TreeFingerprint.Of(source.Root));
        Assert.Equal(MirrorLayout.FullPath, Assert.Single(drill.Bindings).Layout);
        Assert.Equal((4, 0), (drill.Outdated, drill.Errors));
        Assert.Equal((4L, 0L), (rehearsal.Outdated, rehearsal.Errors));
        TempTree.AssertEmpty(scratch.Root);
    }

    [Fact]
    public async Task ANewerArchiveThatCannotBeOpened_IsNamed_WhenAnOlderOneIsRead()
    {
        // The newest weekly zip was cut short by a full disk: the drill reads last month's, and says why.
        using var source = new TempTree();
        using var folder = new TempTree();
        WriteDocuments(source, 3);
        Zip(source, folder, "Docs 2026-10-01.zip", LastWeek.AddDays(-30));
        var broken = folder.At("Docs 2026-11-15.zip");
        File.WriteAllBytes(broken, [0x50, 0x4B, 0x03, 0x04, 1, 2, 3]);
        File.SetLastWriteTimeUtc(broken, LastWeek.UtcDateTime);

        var result = await DrillAsync(source, Archives(folder.Root));

        Assert.Equal(3, result.Verified);
        Assert.Contains("Docs 2026-10-01.zip was read, but a newer archive could not be: Docs 2026-11-15.zip, the zip could not be opened", result.Sentence);
        Assert.NotNull(Assert.Single(result.Bindings).Warning);
    }

    [Fact]
    public async Task AZipOfTheFolderItself_IsFound()
    {
        using var source = new TempTree();
        using var folder = new TempTree();
        WriteDocuments(source, 5);
        var zip = Zip(source, folder, "backup.zip", LastWeek, withFolder: true);

        var result = await DrillAsync(source, Archives(zip));

        Assert.Equal(5, result.Verified);
        Assert.Equal(MirrorLayout.RootFolder, Assert.Single(result.Bindings).Layout);
    }

    [Fact]
    public async Task ChangesAfterTheZip_AreBehind_NewFilesAreMissing_AndDamageIsCorruption()
    {
        using var source = new TempTree();
        using var folder = new TempTree();
        WriteDocuments(source, 6);
        source.Write("docs/random.bin", RandomBytes(200_000), LastWeek);
        var zip = Zip(source, folder, "Docs.zip", LastWeek);
        source.Write("docs/2.txt", "document 2, edited", DateTimeOffset.UtcNow.AddHours(-3));
        source.Write("docs/new.txt", "made after the zip", DateTimeOffset.UtcNow.AddHours(-3));

        var behind = await DrillAsync(source, Archives(folder.Root));
        Assert.Equal((6, 1, 1), (behind.Verified, behind.Outdated, behind.Missing));
        Assert.Equal("not in Docs.zip", Assert.Single(behind.Findings, f => f.Outcome == FileOutcome.Missing).Detail);

        var bytes = File.ReadAllBytes(zip);
        var start = FindEntryData(bytes, "docs/random.bin");
        for (var i = start + 50_000; i < start + 50_064; i++) bytes[i] ^= 0xFF;
        File.WriteAllBytes(zip, bytes);

        var damaged = await DrillAsync(source, Archives(folder.Root));
        Assert.Equal(1, damaged.Corrupt);
        Assert.Equal("docs/random.bin", Assert.Single(damaged.Findings, f => f.Outcome == FileOutcome.Corrupt).RelativePath.Replace('\\', '/'));
    }

    [Fact]
    public async Task AFolderWithoutArchives_OrNoFolder_IsUnreachable()
    {
        using var source = new TempTree();
        using var folder = new TempTree();
        WriteDocuments(source, 2);
        folder.Write("notes.txt", "not an archive");

        var none = await DrillAsync(source, Archives(folder.Root));
        Assert.False(none.Reachable);
        Assert.Equal(folder.Root + " holds no zip or 7z archives", none.ProbeDetail);

        var gone = await DrillAsync(source, Archives(folder.At("unplugged")));
        Assert.Equal(folder.At("unplugged") + " was not found", gone.ProbeDetail);
    }

    [Fact]
    public async Task AScanAndARehearsal_ReadZipsToo()
    {
        using var source = new TempTree();
        using var folder = new TempTree();
        using var scratch = new TempTree();
        WriteDocuments(source, 6);
        Zip(source, folder, "Docs.zip", LastWeek);
        source.Write("docs/new.txt", "made after the zip", DateTimeOffset.UtcNow.AddHours(-1));

        var scan = await new OnlyHereScanner().ScanAsync([Source.FromPath(source.Root, "Docs")], [Archives(folder.Root)], scratch.Root);
        Assert.Equal(1, Assert.Single(scan.Sources).NoCopyFiles);

        var rehearsal = await new RehearsalRunner().RunAsync(new RehearsalPlan(Source.FromPath(source.Root, "Docs"), Archives(folder.Root), scratch.Root)
        {
            BatchBytes = 500,
        });
        Assert.Equal((7L, 6L, 1L), (rehearsal.Files, rehearsal.Verified, rehearsal.Missing));
        TempTree.AssertEmpty(scratch.Root);
    }

    [Fact]
    public async Task ZipNamesInTheLocalCodePage_AreReadAsWindowsWroteThem()
    {
        using var source = new TempTree();
        using var folder = new TempTree();
        source.Write("문서/목록.txt", "a list", LastWeek);
        source.Write("plain.txt", "ascii", LastWeek);

        // As 7-Zip and older Windows write a zip on Korean Windows: names in code page 949, not marked as UTF-8.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using (var zip = new ZipArchive(File.Create(folder.At("Docs.zip")), ZipArchiveMode.Create, leaveOpen: false, Encoding.GetEncoding(949)))
        {
            zip.CreateEntryFromFile(source.At("문서/목록.txt"), "문서/목록.txt");
            zip.CreateEntryFromFile(source.At("plain.txt"), "plain.txt");
        }

        var result = await DrillAsync(source, Archives(folder.Root), new ArchiveAdapter(zipCodePage: 949));
        Assert.Equal((2, 0), (result.Verified, result.Missing));

        // Valid UTF-8 without the mark (as macOS writes) is read as UTF-8 whatever the code page.
        var names = new ArchiveAdapter.ZipNameEncoding(949);
        Assert.Equal("회의록.txt", names.GetString(Encoding.UTF8.GetBytes("회의록.txt")));
        Assert.Equal("목록.txt", names.GetString(Encoding.GetEncoding(949).GetBytes("목록.txt")));
    }

    [Fact]
    public void ZipNames_DefaultToTheSystemsOemCodePage_NotUtf8()
    {
        // Asked of the process, Windows answers 65001 inside Boatdrill.exe, whose manifest makes its code page UTF-8;
        // the zips themselves were written by programs that use the system's (949 on Korean Windows).
        var codePage = ArchiveAdapter.OemCodePage();

        Assert.NotEqual(65001, codePage);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Assert.Equal(codePage, Encoding.GetEncoding(codePage).CodePage);
    }

    [SevenZipFact]
    public async Task PasswordProtectedArchives_AreNotReadAsDamage_AndNeverWaitForAPassword()
    {
        using var source = new TempTree();
        WriteDocuments(source, 4);

        foreach (var (name, switches) in new (string, string[])[]
                 {
                     ("Docs.zip", ["-tzip", "-psecret"]),
                     ("Docs.7z", ["-psecret"]),
                     ("Headers.7z", ["-psecret", "-mhe=on"]),
                 })
        {
            using var folder = new TempTree();
            SevenZipTool.Pack(source.Root, folder.At(name), switches);

            var watch = Stopwatch.StartNew();
            var result = await DrillAsync(source, Archives(folder.Root));

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"{name} took {watch.Elapsed}");
            Assert.Equal((0, 0, 4), (result.Verified, result.Corrupt, result.Missing));
            Assert.Contains($"{name}: it is password-protected, and Boatdrill cannot open encrypted archives yet", result.Sentence);
        }
    }

    [SevenZipFact]
    public async Task ASolid7z_IsExtractedInOnePass_AndItsCrcCatchesDamage()
    {
        using var source = new TempTree();
        using var folder = new TempTree();
        WriteDocuments(source, 6);
        source.Write("docs/report [final].txt", "brackets", LastWeek);
        source.Write("docs/#notes.txt", "a hash sign", LastWeek);
        source.Write("docs/random.bin", RandomBytes(300_000), LastWeek);
        var archive = folder.At("Docs.7z");
        SevenZipTool.Pack(source.Root, archive);

        var result = await DrillAsync(source, Archives(folder.Root));
        Assert.True(result.Reachable, result.ProbeDetail);
        Assert.Equal(9, result.Verified);

        var bytes = File.ReadAllBytes(archive);
        for (var i = bytes.Length / 2; i < bytes.Length / 2 + 64; i++) bytes[i] ^= 0xFF;
        File.WriteAllBytes(archive, bytes);

        var damaged = await DrillAsync(source, Archives(folder.Root));
        Assert.True(damaged.Corrupt >= 1);
        Assert.Contains(damaged.Findings, f => f.Outcome == FileOutcome.Corrupt && f.Detail!.StartsWith("the copy in the archive is damaged", StringComparison.Ordinal));
    }

    [Fact]
    public void SevenZipListings_Parse_AndFoldersAreLeftOut()
    {
        var entries = ArchiveAdapter.ParseSevenZipListing("""
            Path = Docs
            Size = 0
            Modified = 2026-10-29 07:45:28.4552017
            Attributes = D drwxr-xr-x

            Path = Docs/a.txt
            Size = 4
            Packed Size = 300031
            Modified = 2026-10-01 12:00:00.0000000
            Attributes = A -rw-r--r--
            CRC = 5D2F1B30
            """);

        var entry = Assert.Single(entries);
        Assert.Equal(("Docs/a.txt", 4L), (entry.Name, entry.Size));
        Assert.Equal(new DateTimeOffset(new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Local)).ToUniversalTime(), entry.ModifiedUtc);
    }

    [Fact]
    public void Locations_AreDescribedInWords_AndPasswordsNeverShow()
    {
        Assert.Equal(@"Archives in D:\Weekly zips", Destination.Describe(DestinationKind.Archive, @"D:\Weekly zips"));
        Assert.Equal(@"D:\Weekly zips\Laptop.7z", Destination.Describe(DestinationKind.Archive, @"D:\Weekly zips\Laptop.7z"));
        Assert.Equal("restic repository at rest:https://max:***@nas:8000/", Destination.Describe(DestinationKind.Restic, "rest:https://max:secret@nas:8000/"));
        Assert.Equal("rclone remote at gdrive:Backups", Destination.Describe(DestinationKind.Rclone, "gdrive:Backups"));
        Assert.Equal(@"E:\Backup", Destination.Describe(DestinationKind.MirrorFolder, @"E:\Backup"));
    }

    /// <summary>Where an entry's compressed bytes start in a zip: after its local header, name and extra field.</summary>
    private static int FindEntryData(byte[] zip, string name)
    {
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        for (var i = 0; i < zip.Length - 30; i++)
        {
            if (zip[i] != 0x50 || zip[i + 1] != 0x4B || zip[i + 2] != 0x03 || zip[i + 3] != 0x04) continue;
            var nameLength = BitConverter.ToUInt16(zip, i + 26);
            var extraLength = BitConverter.ToUInt16(zip, i + 28);
            if (nameLength == nameBytes.Length && zip.AsSpan(i + 30, nameLength).SequenceEqual(nameBytes)) return i + 30 + nameLength + extraLength;
        }

        throw new InvalidOperationException(name + " is not in the zip");
    }

    private static byte[] RandomBytes(int count)
    {
        var bytes = new byte[count];
        new Random(11).NextBytes(bytes);
        return bytes;
    }
}
