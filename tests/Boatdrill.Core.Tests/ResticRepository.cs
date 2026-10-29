using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Boatdrill.Core.Tests;

/// <summary>
/// A real restic repository in a temp folder, made and filled with the restic on the PATH (or the one named
/// by BOATDRILL_RESTIC). Tests that need one are <see cref="ResticFactAttribute"/>s and skip without restic.
/// </summary>
internal sealed class ResticRepository : IDisposable
{
    public const string Password = "correct horse battery staple";

    public static string? Tool { get; } = Find();

    private readonly TempTree _folder = new();

    static ResticRepository()
    {
        // The adapter's restic runs inherit this, so tests leave the user's own restic cache alone.
        Environment.SetEnvironmentVariable("RESTIC_CACHE_DIR", Path.Combine(Path.GetTempPath(), "boatdrill-tests", "restic-cache"));
    }

    public ResticRepository() => Run("init");

    public string Location => _folder.At("repo");

    public Destination Destination(string? password = Password) => Core.Destination.Restic(Location, password, "Repository", Tool);

    public void Backup(string path, params string[] options) => Run(["backup", path, "--host", Environment.MachineName, .. options]);

    /// <summary>Flips bytes in the middle of the largest data pack, where the biggest file's contents are.</summary>
    public void DamageLargestPack()
    {
        var pack = new DirectoryInfo(Path.Combine(Location, "data")).EnumerateFiles("*", SearchOption.AllDirectories).MaxBy(f => f.Length)!;
        pack.Attributes = FileAttributes.Normal;
        var bytes = File.ReadAllBytes(pack.FullName);
        for (var i = bytes.Length / 2; i < bytes.Length / 2 + 64; i++) bytes[i] ^= 0xFF;
        File.WriteAllBytes(pack.FullName, bytes);
    }

    /// <summary>Every folder and file in the repository with its size, write time and hash, to show a drill left it alone.</summary>
    public string Fingerprint()
    {
        var text = new StringBuilder();
        foreach (var entry in new DirectoryInfo(Location).EnumerateFileSystemInfos("*", SearchOption.AllDirectories).OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            text.Append(Path.GetRelativePath(Location, entry.FullName)).Append(' ').Append(entry.LastWriteTimeUtc.Ticks);
            if (entry is FileInfo file) text.Append(' ').Append(file.Length).Append(' ').Append(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file.FullName))));
            text.AppendLine();
        }

        return text.ToString();
    }

    private void Run(params string[] arguments)
    {
        var info = new ProcessStartInfo(Tool!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.ArgumentList.Add("--quiet");
        info.Environment["RESTIC_REPOSITORY"] = Location;
        info.Environment["RESTIC_PASSWORD"] = Password;

        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEndAsync();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"restic {string.Join(' ', arguments)} failed: {error.Result}");
    }

    private static string? Find()
    {
        var named = Environment.GetEnvironmentVariable("BOATDRILL_RESTIC");
        if (!string.IsNullOrEmpty(named)) return File.Exists(named) ? named : null;

        var names = OperatingSystem.IsWindows() ? new[] { "restic.exe" } : ["restic"];
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(folder => names.Select(name => Path.Combine(folder, name)))
            .FirstOrDefault(File.Exists);
    }

    public void Dispose()
    {
        // restic makes its files read-only, which stops Windows deleting them.
        try
        {
            foreach (var file in Directory.EnumerateFiles(_folder.Root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        _folder.Dispose();
    }
}

/// <summary>A test that needs the real restic, skipped when it is not installed.</summary>
public sealed class ResticFactAttribute : FactAttribute
{
    public ResticFactAttribute()
    {
        if (ResticRepository.Tool is null) Skip = "restic is not installed: put it on the PATH or set BOATDRILL_RESTIC";
    }
}
