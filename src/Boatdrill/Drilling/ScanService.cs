using System.Text.Json;
using Boatdrill.Core;
using Boatdrill.Core.Scan;
using Boatdrill.Core.Settings;
using Boatdrill.Platform;

namespace Boatdrill.Drilling;

/// <summary>
/// Runs the "only on this PC" scan, one at a time and only when asked, and keeps the last report, on disk
/// too so the page has something to show after a restart. As with <see cref="DrillService"/>, events are
/// raised on the scan's thread.
/// </summary>
public sealed class ScanService : IDisposable
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    private readonly SettingsStore _settings;
    private readonly DrillService _drills;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cancel;
    private bool _disposed;

    public ScanService(SettingsStore settings, DrillService drills)
    {
        _settings = settings;
        _drills = drills;

        try
        {
            if (File.Exists(Paths.OnlyHereFile))
            {
                Latest = JsonSerializer.Deserialize<OnlyHereReport>(File.ReadAllText(Paths.OnlyHereFile), BoatdrillJson.Options);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn("Could not read the last scan.", ex);
        }
    }

    public OnlyHereReport? Latest { get; private set; }

    public bool IsRunning { get; private set; }

    public ScanProgress? Progress { get; private set; }

    /// <summary>Why the last scan did not run or finish. Cleared when a scan starts.</summary>
    public string? LastError { get; private set; }

    public event EventHandler? StateChanged;

    public async Task<OnlyHereReport?> RunAsync()
    {
        if (_disposed || !await _gate.WaitAsync(0).ConfigureAwait(false)) return null;

        var settings = _settings.Snapshot();
        var refusal = !settings.IsReadyToDrill ? "Choose a folder to protect and add the place its backup goes first."
            : _drills.IsRunning ? "A drill is running. Look again when it has finished."
            : null;
        if (refusal is not null)
        {
            LastError = refusal;
            _gate.Release();
            StateChanged?.Invoke(this, EventArgs.Empty);
            return null;
        }

        var cancel = new CancellationTokenSource();
        _cancel = cancel;
        IsRunning = true;
        Progress = null;
        LastError = null;
        StateChanged?.Invoke(this, EventArgs.Empty);

        OnlyHereReport? report = null;
        try
        {
            var sources = settings.Sources.Select(s => s.ToSource()).ToList();
            var destinations = settings.Destinations.Select(d => d.ToDestination(Dpapi.Unprotect)).ToList();
            Log.Info($"Scan starting: {sources.Count} sources, {destinations.Count} destinations.");
            report = await Task.Run(() => new OnlyHereScanner().ScanAsync(sources, destinations, Paths.ScratchDirectory, new ProgressSink(this), cancel.Token),
                cancel.Token).ConfigureAwait(false);
            Latest = report;
            Log.Info($"Scan finished: {report.OnlyHereFiles:N0} files only on this PC.");
            Save(report);
        }
        catch (OperationCanceledException)
        {
            LastError = "The scan was stopped before it finished.";
        }
        catch (Exception ex)
        {
            Log.Error("Scan failed.", ex);
            LastError = "The scan could not finish: " + ex.Message;
        }
        finally
        {
            _cancel = null;
            cancel.Dispose();
            IsRunning = false;
            Progress = null;
            _gate.Release();
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        return report;
    }

    public void Cancel()
    {
        try
        {
            _cancel?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The scan finished between the check and the cancel.
        }
    }

    private static void Save(OnlyHereReport report)
    {
        try
        {
            var temporary = Paths.OnlyHereFile + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(report, BoatdrillJson.Options));
            File.Move(temporary, Paths.OnlyHereFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Could not save the scan.", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cancel();
    }

    private sealed class ProgressSink(ScanService owner) : IProgress<ScanProgress>
    {
        private long _lastAnnounced;

        public void Report(ScanProgress value)
        {
            owner.Progress = value;
            var now = Environment.TickCount64;
            if (now - _lastAnnounced < ProgressInterval.TotalMilliseconds) return;
            _lastAnnounced = now;
            owner.StateChanged?.Invoke(owner, EventArgs.Empty);
        }
    }
}
