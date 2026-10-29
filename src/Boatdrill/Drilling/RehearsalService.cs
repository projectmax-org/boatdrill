using System.Text.Json;
using Boatdrill.Core;
using Boatdrill.Core.Drill;
using Boatdrill.Core.Settings;
using Boatdrill.Platform;

namespace Boatdrill.Drilling;

/// <summary>
/// Runs a full-restore rehearsal when asked, one at a time and never beside a drill or a scan, and keeps the
/// last result, on disk too. As with <see cref="DrillService"/>, events are raised on the rehearsal's thread.
/// </summary>
public sealed class RehearsalService : IDisposable
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    private readonly SettingsStore _settings;
    private readonly DrillService _drills;
    private readonly ScanService _scans;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cancel;
    private bool _disposed;

    public RehearsalService(SettingsStore settings, DrillService drills, ScanService scans)
    {
        _settings = settings;
        _drills = drills;
        _scans = scans;

        try
        {
            if (File.Exists(Paths.RehearsalFile))
            {
                Latest = JsonSerializer.Deserialize<RehearsalResult>(File.ReadAllText(Paths.RehearsalFile), BoatdrillJson.Options);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn("Could not read the last rehearsal.", ex);
        }
    }

    public RehearsalResult? Latest { get; private set; }

    public bool IsRunning { get; private set; }

    public RehearsalProgress? Progress { get; private set; }

    /// <summary>Why the last rehearsal did not run or finish. Cleared when one starts.</summary>
    public string? LastError { get; private set; }

    public event EventHandler? StateChanged;

    /// <param name="folder">A folder inside the source, by its path below the source's root; empty for all of it.</param>
    public async Task<RehearsalResult?> RunAsync(string sourceId, string destinationId, string folder)
    {
        if (_disposed || !await _gate.WaitAsync(0).ConfigureAwait(false)) return null;

        var settings = _settings.Snapshot();
        var source = settings.Sources.FirstOrDefault(s => s.Id == sourceId);
        var destination = settings.Destinations.FirstOrDefault(d => d.Id == destinationId);
        var refusal = source is null || destination is null ? "Choose a protected folder and the backup to restore it from."
            : _drills.IsRunning ? "A drill is running. Rehearse when it has finished."
            : _scans.IsRunning ? "A scan is running. Rehearse when it has finished."
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

        RehearsalResult? result = null;
        try
        {
            var plan = new RehearsalPlan(source!.ToSource(), destination!.ToDestination(Dpapi.Unprotect), Paths.ScratchDirectory) { Folder = folder };
            Log.Info($"Rehearsal starting: {source.Name}{(folder.Length == 0 ? string.Empty : "\\" + folder)} from {destination.Name}.");
            result = await Task.Run(() => new RehearsalRunner().RunAsync(plan, new ProgressSink(this), cancel.Token), cancel.Token).ConfigureAwait(false);
            Latest = result;
            Log.Info($"Rehearsal finished: {result.Verified:N0} of {result.Files:N0} files intact.");
            Save(result);
        }
        catch (OperationCanceledException)
        {
            LastError = "The rehearsal was stopped before it finished.";
        }
        catch (Exception ex)
        {
            Log.Error("Rehearsal failed.", ex);
            LastError = "The rehearsal could not finish: " + ex.Message;
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
        return result;
    }

    public void Cancel()
    {
        try
        {
            _cancel?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The rehearsal finished between the check and the cancel.
        }
    }

    private static void Save(RehearsalResult result)
    {
        try
        {
            var temporary = Paths.RehearsalFile + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(result, BoatdrillJson.Options));
            File.Move(temporary, Paths.RehearsalFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Could not save the rehearsal.", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cancel();
    }

    private sealed class ProgressSink(RehearsalService owner) : IProgress<RehearsalProgress>
    {
        private long _lastAnnounced;

        public void Report(RehearsalProgress value)
        {
            owner.Progress = value;
            var now = Environment.TickCount64;
            if (now - _lastAnnounced < ProgressInterval.TotalMilliseconds) return;
            _lastAnnounced = now;
            owner.StateChanged?.Invoke(owner, EventArgs.Empty);
        }
    }
}
