using Boatdrill.Core.Drill;
using Boatdrill.Core.History;
using Boatdrill.Core.Settings;
using Boatdrill.Platform;

namespace Boatdrill.Drilling;

/// <summary>
/// Runs drills, one at a time, and keeps the latest results. Everything a drill needs is read from a
/// snapshot of the settings taken as it starts, so editing settings meanwhile is safe. Events are raised
/// on whatever thread the drill is on; the view models move them to the UI thread.
/// </summary>
public sealed class DrillService : IDisposable
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>Drills kept in memory for the score trend on each card.</summary>
    public const int RecentCount = 12;

    private readonly SettingsStore _settings;
    private readonly HistoryStore _history;
    private readonly LedgerStore _ledger = new(Paths.LedgerFile);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cancel;
    private IReadOnlyList<DrillReport> _recent = [];
    private bool _disposed;

    public DrillService(SettingsStore settings, HistoryStore history)
    {
        _settings = settings;
        _history = history;

        try
        {
            _recent = history.ReadRecent(RecentCount);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Could not read the drill history.", ex);
        }
    }

    public DrillReport? Latest => _recent.Count > 0 ? _recent[^1] : null;

    /// <summary>The last few drills, oldest first. Replaced whole when a drill finishes, so it is safe to read from any thread.</summary>
    public IReadOnlyList<DrillReport> Recent => _recent;

    public bool IsRunning { get; private set; }

    public DrillTrigger? RunningTrigger { get; private set; }

    public DrillProgress? Progress { get; private set; }

    /// <summary>Why the last drill did not finish, in words for the status line. Cleared when a drill starts.</summary>
    public string? LastError { get; private set; }

    /// <summary>Running state or progress changed. Raised at most every 200 ms while a drill reads files.</summary>
    public event EventHandler? StateChanged;

    /// <summary>A drill finished and is in the history.</summary>
    public event EventHandler<DrillReport>? Completed;

    /// <summary>Runs a drill now. Returns null when one is already running, when there is nothing to drill, or when it fails.</summary>
    public async Task<DrillReport?> RunAsync(DrillTrigger trigger)
    {
        if (_disposed || !await _gate.WaitAsync(0).ConfigureAwait(false)) return null;

        var settings = _settings.Snapshot();
        if (!settings.IsReadyToDrill)
        {
            LastError = "Choose a folder to protect and add the place its backup goes, then run a drill.";
            _gate.Release();
            StateChanged?.Invoke(this, EventArgs.Empty);
            return null;
        }

        var cancel = new CancellationTokenSource();
        _cancel = cancel;
        IsRunning = true;
        RunningTrigger = trigger;
        Progress = null;
        LastError = null;
        StateChanged?.Invoke(this, EventArgs.Empty);

        // A scheduled drill runs at background priority; one the user asked for runs at full speed.
        var background = trigger == DrillTrigger.Scheduled && BackgroundMode.Enter();
        DrillReport? report = null;
        try
        {
            var plan = new DrillPlan
            {
                Sources = settings.Sources.Select(s => s.ToSource()).ToList(),
                Destinations = settings.Destinations.Select(d => d.ToDestination(Dpapi.Unprotect)).ToList(),
                ScratchDirectory = Paths.ScratchDirectory,
                Sampling = settings.ToSampleOptions(),
                Trigger = trigger,
                UseCanaries = settings.UseCanaries,
            };

            Log.Info($"{trigger} drill starting: {plan.Sources.Count} sources, {plan.Destinations.Count} destinations.");
            report = await Task.Run(async () =>
            {
                var ledger = settings.RecheckForBitRot ? _ledger.Load() : null;
                var runner = new DrillRunner(reachabilityHistory: id => _history.Reachability(id), ledger: ledger);
                var finished = await runner.RunAsync(plan, new ProgressSink(this), cancel.Token).ConfigureAwait(false);
                if (ledger is not null) await SaveLedgerAsync(ledger, settings).ConfigureAwait(false);
                return finished;
            }, cancel.Token).ConfigureAwait(false);
            await _history.AppendAsync(report, CancellationToken.None).ConfigureAwait(false);
            _recent = [.. _recent.TakeLast(RecentCount - 1), report];
            Log.Info($"Drill {report.Id} finished in {report.Duration.TotalSeconds:0}s; worst verdict {report.Worst}.");
        }
        catch (OperationCanceledException)
        {
            Log.Info("Drill stopped before it finished.");
            LastError = "The drill was stopped before it finished.";
            report = null;
        }
        catch (Exception ex)
        {
            Log.Error("Drill failed.", ex);
            LastError = "The drill could not finish: " + ex.Message;
            report = null;
        }
        finally
        {
            if (background) BackgroundMode.Leave();
            _cancel = null;
            cancel.Dispose();
            IsRunning = false;
            RunningTrigger = null;
            Progress = null;
            _gate.Release();
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        if (report is not null) Completed?.Invoke(this, report);
        return report;
    }

    /// <summary>Keeps the ledger to what is still set up. A ledger that cannot be saved costs re-checks, not the drill.</summary>
    private async Task SaveLedgerAsync(HashLedger ledger, AppSettings settings)
    {
        try
        {
            ledger.Keep(settings.Destinations.Select(d => d.Id).ToList(), settings.Sources.Select(s => s.Id).ToList());
            await _ledger.SaveAsync(ledger).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Could not save the hash ledger.", ex);
        }
    }

    /// <summary>Stops the running drill, if any. Nothing it read is kept.</summary>
    public void Cancel()
    {
        try
        {
            _cancel?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The drill finished between the check and the cancel.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cancel();
    }

    /// <summary>Records progress synchronously on the drill's thread and announces it, at most every 200 ms.</summary>
    private sealed class ProgressSink(DrillService owner) : IProgress<DrillProgress>
    {
        private long _lastAnnounced;

        public void Report(DrillProgress value)
        {
            owner.Progress = value;
            var now = Environment.TickCount64;
            if (now - _lastAnnounced < ProgressInterval.TotalMilliseconds && value.FilesDone != value.FilesTotal) return;
            _lastAnnounced = now;
            owner.StateChanged?.Invoke(owner, EventArgs.Empty);
        }
    }
}
