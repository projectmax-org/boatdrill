using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Boatdrill.Platform;
using Boatdrill.UI;
using Boatdrill.UI.ViewModels;

namespace Boatdrill;

public partial class MainWindow : Window
{
    private readonly AppServices _services;
    private readonly MainViewModel _viewModel;
    private bool _reallyClosing;
    private bool _closing;

    /// <summary>The size the window opened at, when the screen had no room for the size in the settings.</summary>
    private Size? _fitted;

    public MainWindow(AppServices services, MainViewModel viewModel)
    {
        _services = services;
        _viewModel = viewModel;

        // Bindings format numbers and dates in this language, and the text picks its fonts by it.
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(Core.Text.Language.Tag);
        InitializeComponent();
        DataContext = viewModel;

        // Never larger than the screen has room for: a 1080p laptop scaled to 150% has 1280 by 672 here, and the
        // window as it is first sized, 740 high and centred, would open with its title bar above the top of it.
        var wanted = new Size(Math.Max(MinWidth, services.Settings.Settings.WindowWidth), Math.Max(MinHeight, services.Settings.Settings.WindowHeight));
        var room = SystemParameters.WorkArea;
        Width = Math.Min(wanted.Width, Math.Max(MinWidth, room.Width));
        Height = Math.Min(wanted.Height, Math.Max(MinHeight, room.Height));
        if (Width < wanted.Width || Height < wanted.Height) _fitted = new Size(Width, Height);

        SourceInitialized += OnSourceInitialized;
        IsVisibleChanged += OnIsVisibleChanged;
        StateChanged += OnStateChanged;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>Closes the window for real, bypassing the close-to-tray behaviour.</summary>
    public void CloseForReal()
    {
        _reallyClosing = true;
        if (!_closing) Close();
    }

    /// <summary>The page currently on show: the dashboard, history or settings view.</summary>
    public FrameworkElement? CurrentView => ContentHost.Children.OfType<FrameworkElement>().FirstOrDefault(v => v.Visibility == Visibility.Visible);

    private bool HidesToTray => _services.Tray is not null && _services.Settings.Settings.KeepRunningInTray;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        ApplyRoundedCorners(handle);
        ApplyImmersiveDarkMode(handle);
        ThemeManager.ThemeChanged += (_, _) => ApplyImmersiveDarkMode(handle);
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            _viewModel.StartTicking();
            return;
        }

        // From an open window to an icon in the notification area: the footprint should follow.
        _viewModel.StopTicking();
        if (!_reallyClosing) MemoryTrimmer.TrimNow();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized) _viewModel.StopTicking();
        else _viewModel.StartTicking();

        UpdateMaximizedInset(VisualTreeHelper.GetDpi(this));
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdateMaximizedInset(newDpi);
    }

    /// <summary>
    /// A maximised window is larger than the screen by its resizing frame on every side. This window draws
    /// all of itself as client area, so its content is pulled in by the frame's width at the display's scaling.
    /// </summary>
    private void UpdateMaximizedInset(DpiScale dpi)
    {
        var inset = new Thickness(0);
        if (WindowState == WindowState.Maximized)
        {
            try
            {
                var ppi = (uint)Math.Round(dpi.PixelsPerInchX);
                var padded = GetSystemMetricsForDpi(SM_CXPADDEDBORDER, ppi);
                var x = (GetSystemMetricsForDpi(SM_CXSIZEFRAME, ppi) + padded) / dpi.DpiScaleX;
                var y = (GetSystemMetricsForDpi(SM_CYSIZEFRAME, ppi) + padded) / dpi.DpiScaleY;
                inset = new Thickness(x, y, x, y);
            }
            catch (Exception ex)
            {
                Log.Warn("Could not measure the window frame for the maximised layout.", ex);
            }
        }

        RootGrid.Margin = inset;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape when HidesToTray:
                if (EscapeBelongsToFocusedControl()) break;
                HideToTray();
                e.Handled = true;
                break;

            case Key.F5 when Keyboard.FocusedElement is not TextBoxBase:
                if (_viewModel.RunDrillCommand.CanExecute(null)) _viewModel.RunDrillCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// True when the focused control has its own use for Escape: leaving a text box, or closing a drop-down.
    /// The next Escape hides the window as usual.
    /// </summary>
    private bool EscapeBelongsToFocusedControl()
    {
        if (Keyboard.FocusedElement is not DependencyObject focused) return false;

        TextBoxBase? textBox = null;
        for (var node = focused; node is not null; node = GetParent(node))
        {
            if (node is ComboBox { IsDropDownOpen: true }) return true;
            textBox ??= node as TextBoxBase;
        }

        if (textBox is null) return false;

        IInputElement target = NavRail.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true) ?? (IInputElement)this;
        Keyboard.Focus(target);
        return true;
    }

    private static DependencyObject? GetParent(DependencyObject node) =>
        (node is Visual or Visual3D ? VisualTreeHelper.GetParent(node) : null) ?? LogicalTreeHelper.GetParent(node);

    private void HideToTray()
    {
        if (_reallyClosing) return;

        Log.Info("Window closed to the notification area.");
        Hide();
        ShowTrayHintOnce();
    }

    /// <summary>The first time the window goes to the tray, say where it went.</summary>
    private void ShowTrayHintOnce()
    {
        if (_services.Settings.Settings.HasShownTrayHint || _services.Tray is null) return;

        _services.Settings.Update(s => s.HasShownTrayHint = true);
        _services.Tray.ShowBalloon(
            Core.Text.Language.Pick("Boatdrill is still running", "Boatdrill은 계속 실행 중입니다"),
            Core.Text.Language.Pick(
                "It runs your drills on schedule from the notification area and stays out of the way until then. Click the icon to open it, or right-click it to quit.",
                "알림 영역에서 정해진 일정에 따라 복원 훈련을 실행하고, 그때까지는 조용히 기다립니다. 아이콘을 클릭하면 창이 열리고, 마우스 오른쪽 단추로 클릭하면 나오는 메뉴에서 끝낼 수 있습니다."));
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (WindowState == WindowState.Normal && !_reallyClosing)
        {
            var (width, height) = (Width, Height);

            // A size the screen imposed is not one the user chose: it is not kept, so the window is its own size
            // again on a larger screen.
            var imposed = _fitted is { } fitted && Math.Abs(width - fitted.Width) <= 1 && Math.Abs(height - fitted.Height) <= 1;
            if (!imposed && (Math.Abs(width - _services.Settings.Settings.WindowWidth) > 1 || Math.Abs(height - _services.Settings.Settings.WindowHeight) > 1))
            {
                _fitted = null;
                _services.Settings.Update(s =>
                {
                    s.WindowWidth = width;
                    s.WindowHeight = height;
                });
            }
        }

        // Closing the window is not quitting: Boatdrill waits in the notification area for the next drill.
        if (!_reallyClosing && HidesToTray)
        {
            e.Cancel = true;
            Dispatcher.BeginInvoke(new Action(HideToTray));
            return;
        }

        base.OnClosing(e);
        _closing = !e.Cancel;

        // Without the tray there is nothing left to come back to, so closing means quitting.
        if (!_reallyClosing && !e.Cancel) (Application.Current as App)?.ExitApplication();
    }

    // ---------------- native window polish ----------------

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWCP_ROUND = 2;

    /// <summary>Windows 11 rounded corners, keeping the window opaque and hardware-composited.</summary>
    private static void ApplyRoundedCorners(IntPtr handle)
    {
        try
        {
            var preference = DWMWCP_ROUND;
            DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        }
        catch (Exception ex)
        {
            Log.Warn("Rounded corners are unavailable on this version of Windows.", ex);
        }
    }

    /// <summary>Matches the window's shadow and resize border to the app theme.</summary>
    private static void ApplyImmersiveDarkMode(IntPtr handle)
    {
        try
        {
            var dark = ThemeManager.IsDark ? 1 : 0;
            DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        }
        catch (Exception ex)
        {
            Log.Warn("Could not set the immersive dark mode attribute.", ex);
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int SM_CXSIZEFRAME = 32;
    private const int SM_CYSIZEFRAME = 33;
    private const int SM_CXPADDEDBORDER = 92;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);
}
