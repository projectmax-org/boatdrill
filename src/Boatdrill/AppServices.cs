using Boatdrill.Core.History;
using Boatdrill.Core.Settings;
using Boatdrill.Drilling;
using Boatdrill.Platform;
using Boatdrill.Tray;

namespace Boatdrill;

/// <summary>
/// Composition root. Everything long-lived is created here, in dependency order, and disposed in reverse.
/// Seven objects, wired by hand: easier to read than a container's registration list.
/// </summary>
public sealed class AppServices : IDisposable
{
    private bool _disposed;

    /// <param name="withTray">False for the view capture, which runs without a notification-area icon.</param>
    public AppServices(bool withTray = true)
    {
        Settings = new SettingsStore(Paths.SettingsFile, DefaultSettings);
        History = new HistoryStore(Paths.HistoryFile);
        Drills = new DrillService(Settings, History);
        Scans = new ScanService(Settings, Drills);
        Rehearsals = new RehearsalService(Settings, Drills, Scans);
        // A drill that falls due while a scan or a rehearsal runs waits for it to finish.
        Scheduler = new DrillScheduler(Settings, Drills, () => Scans.IsRunning || Rehearsals.IsRunning);
        Tray = withTray ? new TrayIcon() : null;
    }

    public SettingsStore Settings { get; }

    public HistoryStore History { get; }

    public DrillService Drills { get; }

    public DrillScheduler Scheduler { get; }

    public ScanService Scans { get; }

    public RehearsalService Rehearsals { get; }

    public TrayIcon? Tray { get; }

    /// <summary>
    /// Documents, Pictures and the desktop: what most people mean by their files. Each is named as Explorer
    /// names it, as a folder added in Settings is: 문서, 사진 and 바탕 화면 on Korean Windows, which is also
    /// the language Boatdrill speaks on a first run.
    /// </summary>
    private static AppSettings DefaultSettings() => AppSettings.CreateDefault(
    [
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
    ], Shell.FolderDisplayName);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Tray?.Dispose();
        Scheduler.Dispose();
        Rehearsals.Dispose();
        Scans.Dispose();
        Drills.Dispose();
    }
}
