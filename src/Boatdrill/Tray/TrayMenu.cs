using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Boatdrill.Platform;
using Boatdrill.UI.ViewModels;
// System.IO is a global using in this project, so Path needs disambiguating.
using Path = System.Windows.Shapes.Path;

namespace Boatdrill.Tray;

/// <summary>
/// The notification-area right-click menu: a themed WPF menu, built once and refreshed as it opens so
/// "Run" and "Stop" and the status line are always current.
/// </summary>
public static class TrayMenu
{
    public static ContextMenu Build(MainViewModel viewModel, Action showWindow, Action<AppSection> showSection, Action exit)
    {
        var menu = new ContextMenu();

        var open = Item("Open Boatdrill", "Icon.Ring", showWindow);
        open.FontWeight = FontWeights.SemiBold;

        var run = Item("Run a drill now", "Icon.Play", () => viewModel.RunDrillCommand.Execute(null));
        var stop = Item("Stop the drill", "Icon.Stop", () => viewModel.StopDrillCommand.Execute(null));
        var status = new MenuItem { IsEnabled = false, Icon = Icon("Icon.Clock") };
        var history = Item("Drill history", "Icon.History", () => showSection(AppSection.History));
        var settings = Item("Settings", "Icon.Settings", () => showSection(AppSection.Settings));
        var quit = Item("Quit Boatdrill", "Icon.Close", exit);

        menu.Items.Add(open);
        menu.Items.Add(new Separator());
        menu.Items.Add(run);
        menu.Items.Add(stop);
        menu.Items.Add(status);
        menu.Items.Add(new Separator());
        menu.Items.Add(history);
        menu.Items.Add(settings);
        menu.Items.Add(new Separator());
        menu.Items.Add(quit);

        menu.Opened += (_, _) =>
        {
            viewModel.Refresh();
            run.Visibility = viewModel.IsRunning ? Visibility.Collapsed : Visibility.Visible;
            run.IsEnabled = viewModel.RunDrillCommand.CanExecute(null);
            stop.Visibility = viewModel.IsRunning ? Visibility.Visible : Visibility.Collapsed;
            status.Header = viewModel.IsRunning ? viewModel.ProgressText : viewModel.NextDrillText;
        };

        return menu;
    }

    private static MenuItem Item(string header, string iconKey, Action action)
    {
        var item = new MenuItem { Header = header, Icon = Icon(iconKey) };
        item.Click += (_, _) =>
        {
            try { action(); }
            catch (Exception ex) { Log.Error("Tray menu action failed: " + header, ex); }
        };
        return item;
    }

    /// <summary>A themed icon for a menu row, from the shared geometry resources.</summary>
    private static Path Icon(string key)
    {
        var path = new Path
        {
            Width = 15,
            Height = 15,
            StrokeThickness = 1.7,
            Fill = Brushes.Transparent,
            Stretch = Stretch.Uniform,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };

        path.SetResourceReference(Path.DataProperty, key);
        path.SetResourceReference(Shape.StrokeProperty, "Brush.TextSecondary");
        return path;
    }
}
