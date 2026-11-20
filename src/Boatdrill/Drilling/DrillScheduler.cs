using Boatdrill.Core.Drill;
using Boatdrill.Core.Scheduling;
using Boatdrill.Core.Settings;
using Boatdrill.Platform;
using Microsoft.Win32;

namespace Boatdrill.Drilling;

/// <summary>
/// Starts scheduled drills.
/// <para>
/// One <see cref="Timer"/>, armed once for the moment the next drill is due. Nothing polls: between
/// drills the app does no work at all. When the timer fires, <see cref="DeferralPolicy"/> looks at what
/// the PC is doing; a drill that has to wait is looked at again ten minutes later. After a drill, or a
/// change of settings, or the PC waking from sleep, the timer is armed afresh from the wall clock.
/// </para>
/// </summary>
public sealed class DrillScheduler : IDisposable
{
    /// <summary>After a drill that failed, the next attempt waits at least this long.</summary>
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromHours(1);

    /// <summary>Longer waits are split, so the timer never relies on a single month-long delay.</summary>
    private static readonly TimeSpan LongestWait = TimeSpan.FromDays(7);

    private readonly SettingsStore _settings;
    private readonly DrillService _drills;
    private readonly Func<bool> _otherWorkRunning;
    private readonly Timer _timer;
    private readonly Lock _gate = new();

    private DateTimeOffset? _dueAt;
    private DateTimeOffset? _lastFailedAt;
    private bool _started;
    private bool _disposed;

    /// <param name="otherWorkRunning">
    /// True while a scan or a full-restore rehearsal runs: a due drill waits for it, as they wait for a drill. A drill
    /// beside a rehearsal would also sweep the scratch folder the rehearsal is restoring into.
    /// </param>
    public DrillScheduler(SettingsStore settings, DrillService drills, Func<bool>? otherWorkRunning = null)
    {
        _settings = settings;
        _drills = drills;
        _otherWorkRunning = otherWorkRunning ?? (() => false);
        _timer = new Timer(_ => OnTimer(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>When the next scheduled drill is due, possibly already past while it waits for a better moment.</summary>
    public DateTimeOffset? NextDrillAt => _dueAt;

    /// <summary>Why the due drill is waiting, or None.</summary>
    public DeferReason LastDeferReason { get; private set; }

    /// <summary>Raised on any thread when the due time or the reason for waiting changes.</summary>
    public event EventHandler? ScheduleChanged;

    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;

        _settings.Changed += OnSettingsChanged;
        _drills.Completed += OnDrillCompleted;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Rearm();
    }

    /// <summary>Works out when the next drill is due and arms the timer for it.</summary>
    public void Rearm()
    {
        if (_disposed || !_started) return;

        var settings = _settings.Settings;
        var now = DateTimeOffset.UtcNow;
        var due = settings.IsReadyToDrill ? DrillSchedule.NextDue(settings.Frequency, _drills.Latest?.FinishedUtc, now) : null;
        if (due is { } d && _lastFailedAt is { } failed && failed + FailureBackoff > d) due = failed + FailureBackoff;

        lock (_gate)
        {
            if (_disposed) return;
            LastDeferReason = DeferReason.None;
            _dueAt = due;
            _timer.Change(due is null ? Timeout.InfiniteTimeSpan : Clamp(due.Value - now), Timeout.InfiniteTimeSpan);
        }

        ScheduleChanged?.Invoke(this, EventArgs.Empty);
    }

    private static TimeSpan Clamp(TimeSpan delay) =>
        delay < TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : delay > LongestWait ? LongestWait : delay;

    private void OnTimer()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (_disposed || _dueAt is not { } due) return;

                var now = DateTimeOffset.UtcNow;
                if (now < due - TimeSpan.FromMinutes(1))
                {
                    // One leg of a long wait; see LongestWait.
                    Rearm();
                    return;
                }

                if (_drills.IsRunning || _otherWorkRunning())
                {
                    RetryLater();
                    return;
                }

                var reason = DeferralPolicy.Evaluate(SystemConditions.Read(), _settings.Settings, now - due);
                if (reason != DeferReason.None)
                {
                    if (reason != LastDeferReason) Log.Info("Scheduled drill is waiting: " + DeferralPolicy.Describe(reason) + ".");
                    LastDeferReason = reason;
                    RetryLater();
                    return;
                }

                LastDeferReason = DeferReason.None;
                var report = await _drills.RunAsync(DrillTrigger.Scheduled).ConfigureAwait(false);
                if (report is null)
                {
                    // Completed re-arms after a drill that finished; one that did not comes back in an hour.
                    _lastFailedAt = DateTimeOffset.UtcNow;
                    Rearm();
                }
            }
            catch (Exception ex)
            {
                Log.Error("Scheduled drill failed to start.", ex);
                _lastFailedAt = DateTimeOffset.UtcNow;
                Rearm();
            }
        });
    }

    private void RetryLater()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _timer.Change(DeferralPolicy.RetryAfter, Timeout.InfiniteTimeSpan);
        }

        ScheduleChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnSettingsChanged(object? sender, AppSettings settings) => Rearm();

    private void OnDrillCompleted(object? sender, DrillReport report)
    {
        _lastFailedAt = null;
        Rearm();
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        // The timer does not count time asleep, so its remaining delay means nothing after a wake.
        if (e.Mode == PowerModes.Resume) Rearm();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        if (_started)
        {
            _settings.Changed -= OnSettingsChanged;
            _drills.Completed -= OnDrillCompleted;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        }

        _timer.Dispose();
    }
}
