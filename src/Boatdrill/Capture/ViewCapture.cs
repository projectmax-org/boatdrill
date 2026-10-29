using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Boatdrill.Core.Settings;
using Boatdrill.Platform;
using Boatdrill.UI;
using Boatdrill.UI.ViewModels;

namespace Boatdrill.Capture;

/// <summary>
/// <c>Boatdrill.exe --capture &lt;folder&gt;</c>: loads every view against sample data in a throwaway data
/// folder, renders each to PNG in the light and the dark theme, and exits. Exit code 1 when anything
/// throws (a XAML error, a missing resource), 2 when a binding names a property that does not exist.
/// The Windows CI job runs it on every push, so the UI is exercised even where nobody can look at it.
/// </summary>
internal static class ViewCapture
{
    private const string Switch = "--capture";

    public static bool TryGetOutput(string[] args, out string output)
    {
        var at = Array.FindIndex(args, a => a.Equals(Switch, StringComparison.OrdinalIgnoreCase));
        output = at >= 0 && at + 1 < args.Length ? args[at + 1] : string.Empty;
        return at >= 0;
    }

    public static async Task<int> RunAsync(string output)
    {
        output = Path.GetFullPath(string.IsNullOrWhiteSpace(output) ? "capture" : output);
        Directory.CreateDirectory(output);

        var bindings = new BindingMessages();
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindings);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;

        try
        {
            var data = Path.Combine(Path.GetTempPath(), "boatdrill-capture-" + Guid.NewGuid().ToString("N")[..8]);
            Paths.UseDataDirectory(data);
            SampleData.Write(data);

            using var services = new AppServices(withTray: false);
            using var viewModel = new MainViewModel(services);
            await viewModel.History.ReloadAsync();

            // The report a user would save from the dashboard, kept with the images.
            if (services.Drills.Latest is { } latest)
            {
                File.WriteAllText(Path.Combine(output, "report.html"), Core.Reporting.HtmlReport.Render(latest, services.Drills.Recent, "capture"));
            }

            var window = new MainWindow(services, viewModel)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
                Width = 1180,
                Height = 800,
                ShowActivated = false,
                ShowInTaskbar = false,
            };
            window.Show();

            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                ThemeManager.Apply(theme);
                foreach (var section in Enum.GetValues<AppSection>())
                {
                    viewModel.Section = section;
                    foreach (var card in viewModel.Dashboard.Cards) card.ShowFindings = card.Id == "nas";
                    await Settle(window);
                    await SavePages(window, Path.Combine(output, $"{Name(section)}-{Name(theme)}"));
                }
            }

            // Adding a restic repository: the form that opens in Settings.
            ThemeManager.Apply(AppTheme.Light);
            viewModel.Section = AppSection.Settings;
            viewModel.Settings.IsAddingRestic = true;
            await Settle(window);
            if (window.CurrentView?.FindName("ResticPassword") is FrameworkElement password)
            {
                password.BringIntoView(new Rect(0, -220, 10, 420));
                await Settle(window);
            }

            Save(window, Path.Combine(output, "settings-add-restic-light.png"));
            viewModel.Settings.CancelRestic();

            // A first run: folders chosen, no destination yet.
            ThemeManager.Apply(AppTheme.Light);
            services.Settings.Update(s => s.Destinations.Clear());
            viewModel.Section = AppSection.Dashboard;
            viewModel.Refresh();
            await Settle(window);
            await SavePages(window, Path.Combine(output, "dashboard-first-run-light"));

            window.CloseForReal();

            File.WriteAllLines(Path.Combine(output, "binding-messages.txt"), bindings.Messages);
            var pathErrors = bindings.Messages.Count(m => m.Contains("BindingExpression path error", StringComparison.Ordinal));
            return pathErrors == 0 ? 0 : 2;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(output, "capture-error.txt"), ex.ToString());
            return 1;
        }
    }

    private static string Name<T>(T value) where T : Enum => value.ToString().ToLowerInvariant();

    /// <summary>Lets layout and rendering catch up with a change of section or theme.</summary>
    private static async Task Settle(Window window)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>The page as it opens, then a further image per screenful until its end, at most five.</summary>
    private static async Task SavePages(MainWindow window, string stem)
    {
        Save(window, stem + ".png");
        if (window.CurrentView is not { } view || FindPageScroller(view) is not { } scroller) return;

        for (var page = 2; page <= 5 && scroller.VerticalOffset + scroller.ViewportHeight < scroller.ExtentHeight - 1; page++)
        {
            scroller.ScrollToVerticalOffset(scroller.VerticalOffset + scroller.ViewportHeight - 60);
            await Settle(window);
            Save(window, $"{stem}-{page}.png");
        }

        scroller.ScrollToTop();
        await Settle(window);
    }

    /// <summary>The page's own scroller: the first one tall enough to hold the page, nearest the top of the tree.</summary>
    private static ScrollViewer? FindPageScroller(DependencyObject root)
    {
        var queue = new Queue<DependencyObject>([root]);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node is ScrollViewer { ActualHeight: > 300 } scroller) return scroller;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) queue.Enqueue(VisualTreeHelper.GetChild(node, i));
        }

        return null;
    }

    private static void Save(Window window, string path)
    {
        var root = (FrameworkElement)window.Content;
        var width = (int)Math.Ceiling(root.ActualWidth);
        var height = (int)Math.Ceiling(root.ActualHeight);

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    /// <summary>Collects WPF's binding diagnostics, one message per entry.</summary>
    private sealed class BindingMessages : TraceListener
    {
        private string _pending = string.Empty;

        public List<string> Messages { get; } = [];

        public override void Write(string? message) => _pending += message;

        public override void WriteLine(string? message)
        {
            Messages.Add(_pending + message);
            _pending = string.Empty;
        }
    }
}
