using System.Windows;
using System.Windows.Threading;
using Boatdrill.Capture;
using Boatdrill.Core.Drill;
using Boatdrill.Core.Notifications;
using Boatdrill.Platform;
using Boatdrill.Tray;
using Boatdrill.UI;
using Boatdrill.UI.ViewModels;

namespace Boatdrill;

public partial class App : Application
{
    private SingleInstance? _instance;
    private AppServices? _services;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private bool _exiting;
    private bool _toldOfFailure;
    private string _language = string.Empty;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // --capture <folder>: render every view with sample results to PNG and exit. For CI and screenshots.
        if (ViewCapture.TryGetOutput(e.Args, out var captureOutput))
        {
            Dispatcher.InvokeAsync(async () => Shutdown(await ViewCapture.RunAsync(captureOutput)));
            return;
        }

        // --quit: ask the running copy to exit, and wait for it, as the installer does before it replaces or
        // removes the app. Exit code 0 once none is running.
        var quit = e.Args.Any(a => a.Equals("--quit", StringComparison.OrdinalIgnoreCase));

        // A second launch brings the running copy forward instead of starting a rival tray icon.
        try
        {
            _instance = SingleInstance.Acquire();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            Log.Warn("Another Boatdrill is running with different rights; this launch ends here.", ex);
            Shutdown(quit ? 1 : 0);
            return;
        }

        if (quit)
        {
            var gone = _instance.IsFirstInstance || _instance.AskFirstInstanceToQuit(TimeSpan.FromSeconds(30));
            _instance.Dispose();
            Shutdown(gone ? 0 : 1);
            return;
        }

        if (!_instance.IsFirstInstance)
        {
            _instance.SignalFirstInstance();
            _instance.Dispose();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Unhandled exception.", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Warn("Unobserved task exception.", args.Exception);
            args.SetObserved();
        };

        Log.Info("Boatdrill starting.");

        try
        {
            _services = new AppServices();
        }
        catch (Exception ex)
        {
            Log.Error("Fatal error during start-up.", ex);
            MessageBox.Show(Core.Text.Language.Pick("Boatdrill could not start.", "Boatdrill을 시작할 수 없습니다.") + "\n\n" + ex.Message, "Boatdrill",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var settings = _services.Settings.Settings;
        ThemeManager.Apply(settings.Theme);
        _language = settings.UiLanguage;
        Core.Text.Language.Use(_language);
        _services.Settings.Changed += (_, changed) =>
        {
            if (changed.UiLanguage != _language) Dispatcher.BeginInvoke(new Action(() => ApplyLanguage(changed.UiLanguage)));
        };

        // Scheduled drills need Boatdrill running, so the first run registers it to start with Windows,
        // as the setting says. From then on the entry belongs to the user and Task Manager.
        if (!settings.HasCompletedFirstRun && settings.StartWithWindows && !StartupManager.HasEntry())
            StartupManager.SetEnabled(true);
        StartupManager.RepairIfNeeded();

        _viewModel = new MainViewModel(_services);
        ConfigureTray();
        _services.Drills.Completed += OnDrillCompleted;
        _services.Drills.Failed += OnDrillFailed;

        _instance.ActivationRequested += (_, _) => Dispatcher.BeginInvoke(new Action(ShowWindow));
        _instance.QuitRequested += (_, _) =>
        {
            Log.Info("Asked to exit by another launch (--quit).");
            Dispatcher.BeginInvoke(new Action(ExitApplication));
        };
        _instance.StartListening();

        // Sign-in launches (the Run key passes --background) go straight to the notification area,
        // except on the very first run, where a hidden app would look like one that failed to start.
        var background = e.Args.Any(a => a.Equals("--background", StringComparison.OrdinalIgnoreCase)) && settings.HasCompletedFirstRun;
        if (background)
        {
            Log.Info("Starting in the notification area.");
            MemoryTrimmer.TrimNow();
        }
        else
        {
            ShowWindow();
        }

        if (!settings.HasCompletedFirstRun) _services.Settings.Update(s => s.HasCompletedFirstRun = true);

        _services.Scheduler.Start();
    }

    private void ConfigureTray()
    {
        if (_services?.Tray is not { } tray || _viewModel is null) return;

        tray.Selected += (_, _) => Dispatcher.BeginInvoke(new Action(ShowWindow));
        tray.DoubleClicked += (_, _) => Dispatcher.BeginInvoke(new Action(ShowWindow));
        tray.BalloonClicked += (_, _) => Dispatcher.BeginInvoke(new Action(() => ShowSection(AppSection.Dashboard)));
        AttachTray();
        tray.Show();
    }

    /// <summary>The tray's menu and tooltip, from the view model of the moment: built again when the language changes.</summary>
    private void AttachTray()
    {
        if (_services?.Tray is not { } tray || _viewModel is not { } viewModel) return;

        tray.Menu = TrayMenu.Build(viewModel, ShowWindow, ShowSection, ExitApplication);
        tray.SetTooltip(viewModel.TrayTooltip);
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.TrayTooltip) && ReferenceEquals(viewModel, _viewModel)) tray.SetTooltip(viewModel.TrayTooltip);
        };
    }

    /// <summary>
    /// Speaks the language chosen in Settings from now on: the window, its pages and the tray menu are built
    /// again in it, on the page that was showing. A drill that is running carries on.
    /// </summary>
    private void ApplyLanguage(string code)
    {
        if (_services is null || _viewModel is null || _exiting || code == _language) return;

        _language = code;
        Core.Text.Language.Use(code);
        Log.Info($"Speaking {(Core.Text.Language.IsKorean ? "Korean" : "English")} from now on.");

        var old = _viewModel;
        var wasOpen = _window is { IsVisible: true };
        _viewModel = new MainViewModel(_services) { Section = old.Section };
        _window?.CloseForReal();
        _window = null;
        old.Dispose();
        AttachTray();
        if (wasOpen) ShowWindow();
    }

    /// <summary>
    /// After a drill: a notification when something needs doing, unless the user is already looking at the
    /// result. A drill that ran with the window closed hands its memory back afterwards.
    /// </summary>
    private void OnDrillCompleted(object? sender, DrillReport report)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_services is null) return;

            _toldOfFailure = false;
            if (_services.Settings.Settings.NotifyOnProblems && !Watching && NotificationPolicy.For(report) is { } notice)
                _services.Tray?.ShowBalloon(notice.Title, notice.Text);

            if (_window is not { IsVisible: true }) MemoryTrimmer.TrimNow();
        }));
    }

    /// <summary>
    /// A drill that could not finish says so in a notification, once until a drill finishes: a scheduled drill that
    /// fails is tried again every hour. The status line says it to a user looking at the window.
    /// </summary>
    private void OnDrillFailed(object? sender, string reason)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_services is null || _toldOfFailure || !_services.Settings.Settings.NotifyOnProblems || Watching) return;

            var notice = NotificationPolicy.ForFailedDrill(reason);
            _services.Tray?.ShowBalloon(notice.Title, notice.Text);
            _toldOfFailure = true;
        }));
    }

    /// <summary>The user is looking at the window, which shows the latest drill already.</summary>
    private bool Watching => _window is { IsVisible: true, IsActive: true } && _window.WindowState != WindowState.Minimized;

    public void ShowSection(AppSection section)
    {
        if (_viewModel is not null) _viewModel.Section = section;
        ShowWindow();
    }

    /// <summary>Brings the window up, creating it when there is none yet or the last one was closed.</summary>
    public void ShowWindow()
    {
        if (_services is null || _viewModel is null || _exiting) return;

        if (_window is null || !_window.IsLoaded) _window = new MainWindow(_services, _viewModel);

        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;

        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
        _window.Focus();
    }

    /// <summary>How the app ends itself. Each step on its own, so one failure cannot cost the settings.</summary>
    public void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;

        Log.Info("Boatdrill shutting down.");
        Attempt(() => _services?.Drills.Cancel());
        Attempt(() => _viewModel?.Dispose());
        Attempt(() => _window?.CloseForReal());
        Attempt(() => _services?.Settings.Save());
        Attempt(() => _services?.Dispose());
        Attempt(() => _instance?.Dispose());

        Shutdown();
    }

    /// <summary>Sign-out, restart and shutdown: stop any drill and save, while the process is sure to be alive.</summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        if (e.Cancel) return;

        Log.Info("Session ending.");
        Attempt(() => _services?.Drills.Cancel());
        Attempt(() => _services?.Settings.Save());
    }

    private static void Attempt(Action step)
    {
        try { step(); }
        catch (Exception ex) { Log.Warn("Error during shutdown.", ex); }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Logging and carrying on keeps the schedule and the tray icon working; a binding error is no reason to quit.
        Log.Error("Unhandled UI exception.", e.Exception);
        e.Handled = true;
    }
}
