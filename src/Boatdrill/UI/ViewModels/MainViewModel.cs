using System.Windows.Threading;
using Boatdrill.Core.Drill;
using Boatdrill.Core.Notifications;
using Boatdrill.Core.Scheduling;
using Boatdrill.Core.Settings;
using Boatdrill.Core.Text;
using Boatdrill.Platform;

namespace Boatdrill.UI.ViewModels;

public enum AppSection
{
    Dashboard,
    OnlyHere,
    Rehearsal,
    History,
    Settings,
}

/// <summary>
/// The window's state: which section is showing, how the running drill is going, when the next one is due.
/// Background events arrive on any thread and are folded into one refresh on the UI thread.
/// </summary>
public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private readonly AppServices _services;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _ticker;
    private AppSection _section;
    private int _refreshQueued;
    private bool _disposed;

    private bool _isRunning;
    private double _progressValue;
    private bool _isProgressIndeterminate;
    private string _progressText = string.Empty;
    private string _nextDrillText = string.Empty;
    private string _statusMessage = string.Empty;
    private bool _statusIsError;
    private string _trayTooltip = "Boatdrill";

    public MainViewModel(AppServices services)
    {
        _services = services;
        _dispatcher = Dispatcher.CurrentDispatcher;

        RunDrillCommand = new RelayCommand(() => _ = _services.Drills.RunAsync(DrillTrigger.Manual),
            () => !_services.Drills.IsRunning && !_services.Rehearsals.IsRunning && _services.Settings.Settings.IsReadyToDrill);
        StopDrillCommand = new RelayCommand(() => _services.Drills.Cancel(), () => _services.Drills.IsRunning);
        ShowSettingsCommand = new RelayCommand(() => Section = AppSection.Settings);

        Dashboard = new DashboardViewModel(services, this);
        OnlyHere = new OnlyHereViewModel(services);
        Rehearsal = new RehearsalViewModel(services);
        History = new HistoryViewModel(services);
        Settings = new SettingsViewModel(services);

        // "Next drill in 5 minutes" has to move while someone is looking at it, and only then.
        _ticker = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
        _ticker.Tick += (_, _) => Refresh();

        _services.Drills.StateChanged += OnBackgroundChange;
        _services.Drills.Completed += OnDrillCompleted;
        _services.Scheduler.ScheduleChanged += OnBackgroundChange;
        _services.Rehearsals.StateChanged += OnBackgroundChange;
        _services.Settings.Changed += OnSettingsChanged;

        Refresh();
    }

    public DashboardViewModel Dashboard { get; }

    public OnlyHereViewModel OnlyHere { get; }

    public RehearsalViewModel Rehearsal { get; }

    public HistoryViewModel History { get; }

    public SettingsViewModel Settings { get; }

    public RelayCommand RunDrillCommand { get; }

    public RelayCommand StopDrillCommand { get; }

    public RelayCommand ShowSettingsCommand { get; }

    public AppSection Section
    {
        get => _section;
        set
        {
            if (!Set(ref _section, value)) return;
            Raise(nameof(IsDashboard), nameof(IsOnlyHere), nameof(IsRehearsal), nameof(IsHistory), nameof(IsSettings), nameof(SectionTitle));
            if (value == AppSection.History) _ = History.ReloadAsync();
        }
    }

    public bool IsDashboard
    {
        get => Section == AppSection.Dashboard;
        set { if (value) Section = AppSection.Dashboard; }
    }

    public bool IsOnlyHere
    {
        get => Section == AppSection.OnlyHere;
        set { if (value) Section = AppSection.OnlyHere; }
    }

    public bool IsRehearsal
    {
        get => Section == AppSection.Rehearsal;
        set { if (value) Section = AppSection.Rehearsal; }
    }

    public bool IsHistory
    {
        get => Section == AppSection.History;
        set { if (value) Section = AppSection.History; }
    }

    public bool IsSettings
    {
        get => Section == AppSection.Settings;
        set { if (value) Section = AppSection.Settings; }
    }

    public string SectionTitle => Section switch
    {
        AppSection.Dashboard => "Your backups",
        AppSection.OnlyHere => "Only on this PC",
        AppSection.Rehearsal => "Full restore",
        AppSection.History => "Drill history",
        _ => "Settings",
    };

    public bool IsRunning
    {
        get => _isRunning;
        private set => Set(ref _isRunning, value);
    }

    public double ProgressValue
    {
        get => _progressValue;
        private set => Set(ref _progressValue, value);
    }

    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set => Set(ref _isProgressIndeterminate, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => Set(ref _progressText, value);
    }

    public string NextDrillText
    {
        get => _nextDrillText;
        private set => Set(ref _nextDrillText, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => Set(ref _statusMessage, value);
    }

    public bool StatusIsError
    {
        get => _statusIsError;
        private set => Set(ref _statusIsError, value);
    }

    /// <summary>The notification-area tooltip: how the backups stand, in one line.</summary>
    public string TrayTooltip
    {
        get => _trayTooltip;
        private set => Set(ref _trayTooltip, value);
    }

    public void StartTicking()
    {
        Refresh();
        _ticker.Start();
    }

    public void StopTicking() => _ticker.Stop();

    public void Refresh()
    {
        if (_disposed) return;

        var drills = _services.Drills;
        var settings = _services.Settings.Settings;
        var latest = drills.Latest;
        var now = DateTimeOffset.UtcNow;

        IsRunning = drills.IsRunning;
        if (drills.IsRunning)
        {
            var progress = drills.Progress;
            IsProgressIndeterminate = progress is null || progress.FilesTotal == 0;
            ProgressValue = progress is { FilesTotal: > 0 } ? (double)progress.FilesDone / progress.FilesTotal : 0;
            ProgressText = progress switch
            {
                null => "Starting the drill…",
                { FilesTotal: > 0 } => $"{progress.Stage}: {progress.FilesDone:N0} of {progress.FilesTotal:N0} files",
                _ => progress.Stage + "…",
            };
        }
        else
        {
            IsProgressIndeterminate = false;
            ProgressValue = 0;
            ProgressText = string.Empty;
        }

        NextDrillText = DescribeNextDrill(settings, latest, now);

        if (!drills.IsRunning && drills.LastError is { } error)
        {
            StatusMessage = error;
            StatusIsError = true;
        }
        else
        {
            StatusMessage = latest is null
                ? "Nothing leaves this PC, and a drill only ever reads from your backups."
                : $"Last drill {latest.StartedUtc.ToLocalTime():dddd d MMMM, HH:mm}, took {Humanize.Duration(latest.Duration)}. Nothing leaves this PC.";
            StatusIsError = false;
        }

        TrayTooltip = DescribeForTray(latest);
        Dashboard.Refresh();
        OnlyHere.Refresh();
        Rehearsal.Refresh();
        RunDrillCommand.RaiseCanExecuteChanged();
        StopDrillCommand.RaiseCanExecuteChanged();
    }

    private string DescribeNextDrill(AppSettings settings, DrillReport? latest, DateTimeOffset now)
    {
        if (settings.Sources.Count == 0) return "Choose the folders to protect in Settings.";
        if (settings.Destinations.Count == 0) return "Add the place your backup goes in Settings.";
        if (settings.Frequency == DrillFrequency.Off) return "Scheduled drills are off. Run one whenever you like.";

        var due = _services.Scheduler.NextDrillAt ?? DrillSchedule.NextDue(settings.Frequency, latest?.FinishedUtc, now);
        if (due is null) return "Scheduled drills are off.";
        if (due.Value > now) return "Next drill " + Humanize.Until(due.Value - now) + ".";

        var reason = _services.Scheduler.LastDeferReason;
        return reason == DeferReason.None
            ? "The next drill is due now."
            : "The next drill is due, " + DeferralPolicy.Describe(reason) + ".";
    }

    private string DescribeForTray(DrillReport? latest)
    {
        if (_services.Drills.IsRunning) return "Boatdrill: a drill is running";
        if (latest is null) return "Boatdrill: no drill has run yet";

        var problems = latest.Destinations.Where(d => NotificationPolicy.IsProblem(d.Score.Verdict)).ToList();
        return problems.Count switch
        {
            0 => "Boatdrill: your backups would restore",
            1 => $"Boatdrill: {problems[0].DestinationName} {VerdictStyle.Text(problems[0].Score.Verdict)}",
            _ => $"Boatdrill: {problems.Count} backups need attention",
        };
    }

    private void OnBackgroundChange(object? sender, EventArgs e) => QueueRefresh();

    private void OnSettingsChanged(object? sender, AppSettings settings) => QueueRefresh();

    private void OnDrillCompleted(object? sender, DrillReport report)
    {
        _dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await History.ReloadAsync();
                Refresh();
            }
            catch (Exception ex)
            {
                Log.Warn("Could not show the finished drill.", ex);
            }
        });
    }

    /// <summary>Many background events become one refresh: progress arrives every 200 ms during a drill.</summary>
    private void QueueRefresh()
    {
        if (_disposed || Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;

        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            Refresh();
        }));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _ticker.Stop();
        _services.Drills.StateChanged -= OnBackgroundChange;
        _services.Drills.Completed -= OnDrillCompleted;
        _services.Scheduler.ScheduleChanged -= OnBackgroundChange;
        _services.Rehearsals.StateChanged -= OnBackgroundChange;
        _services.Settings.Changed -= OnSettingsChanged;
        OnlyHere.Dispose();
        Rehearsal.Dispose();
        Settings.Dispose();
    }
}
