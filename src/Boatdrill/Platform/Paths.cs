namespace Boatdrill.Platform;

/// <summary>Every on-disk location the app uses. Created on first access.</summary>
public static class Paths
{
    /// <summary>Points the app at another data folder: for the view capture, and for trying a build without touching real settings.</summary>
    public const string DataDirectoryVariable = "BOATDRILL_DATA_DIR";

    private static string? _data;

    /// <summary>%LOCALAPPDATA%\Boatdrill: settings, history, log and the drill scratch folder.</summary>
    public static string DataDirectory => _data ??= EnsureDirectory(
        Environment.GetEnvironmentVariable(DataDirectoryVariable) is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Boatdrill"));

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    /// <summary>One JSON line per drill; see Boatdrill.Core.History.HistoryStore.</summary>
    public static string HistoryFile => Path.Combine(DataDirectory, "history.jsonl");

    public static string LogFile => Path.Combine(DataDirectory, "boatdrill.log");

    /// <summary>Copies verified in past drills, re-checked for bit rot; see Boatdrill.Core.History.LedgerStore.</summary>
    public static string LedgerFile => Path.Combine(DataDirectory, "ledger.json");

    /// <summary>The last "only on this PC" scan, so the page has something to show after a restart.</summary>
    public static string OnlyHereFile => Path.Combine(DataDirectory, "only-here.json");

    /// <summary>Where sampled copies are read back to during a drill. Emptied again when the drill ends.</summary>
    public static string ScratchDirectory => EnsureDirectory(Path.Combine(DataDirectory, "scratch"));

    /// <summary>Used once, before anything else reads a path.</summary>
    internal static void UseDataDirectory(string directory) => _data = EnsureDirectory(directory);

    public static string EnsureDirectory(string path)
    {
        if (!Directory.Exists(path)) Directory.CreateDirectory(path);
        return path;
    }
}
