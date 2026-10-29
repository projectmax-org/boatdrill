using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Boatdrill.Core.Tests;

/// <summary>
/// A real Kopia filesystem repository in a temp folder, made and filled with the kopia on the PATH (or the one named
/// by BOATDRILL_KOPIA), through a connection of its own. Tests that need one are <see cref="KopiaFactAttribute"/>s
/// and skip without kopia.
/// </summary>
internal sealed class KopiaRepository : IDisposable
{
    public const string Password = "correct horse battery staple";

    public static string? Tool { get; } = Find();

    private readonly TempTree _folder = new();

    public KopiaRepository() => Run("repository", "create", "filesystem", "--path", Location, "--cache-directory", _folder.At("cache"));

    public string Location => _folder.At("repo");

    /// <summary>The configuration file of the test's own connection, as KopiaUI keeps one for the user's.</summary>
    public string ConfigFile => _folder.At("connection/repository.config");

    public Destination Destination(string? password = Password) => Core.Destination.Kopia(Location, password, "Repository", Tool);

    public void Backup(string path, params string[] options) => Run(["snapshot", "create", path, .. options]);

    /// <summary>Flips bytes in the middle of the largest data pack, where the biggest file's contents are.</summary>
    public void DamageLargestPack()
    {
        var pack = new DirectoryInfo(Path.Combine(Location, "p")).EnumerateFiles("*.f", SearchOption.AllDirectories).MaxBy(f => f.Length)!;
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
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["KOPIA_CONFIG_PATH"] = ConfigFile;
        info.Environment["KOPIA_PASSWORD"] = Password;
        info.Environment["KOPIA_LOG_DIR"] = _folder.At("logs");
        info.Environment["KOPIA_CHECK_FOR_UPDATES"] = "false";
        info.Environment["KOPIA_PERSIST_CREDENTIALS_ON_CONNECT"] = "false";
        info.Environment["KOPIA_SEND_ERROR_NOTIFICATIONS"] = "never";

        using var process = Process.Start(info)!;
        process.StandardInput.Close();
        var error = process.StandardError.ReadToEndAsync();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"kopia {string.Join(' ', arguments)} failed: {error.Result}");
    }

    private static string? Find()
    {
        var named = Environment.GetEnvironmentVariable("BOATDRILL_KOPIA");
        if (!string.IsNullOrEmpty(named)) return File.Exists(named) ? named : null;

        var names = OperatingSystem.IsWindows() ? new[] { "kopia.exe" } : ["kopia"];
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(folder => names.Select(name => Path.Combine(folder, name)))
            .FirstOrDefault(File.Exists);
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_folder.Root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        _folder.Dispose();
    }
}

/// <summary>A test that needs the real kopia, skipped when it is not installed.</summary>
public sealed class KopiaFactAttribute : FactAttribute
{
    public KopiaFactAttribute()
    {
        if (KopiaRepository.Tool is null) Skip = "kopia is not installed: put it on the PATH or set BOATDRILL_KOPIA";
    }
}
