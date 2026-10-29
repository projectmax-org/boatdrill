using System.Windows;
using System.Windows.Controls;
using Boatdrill.Platform;
using Boatdrill.UI.ViewModels;

namespace Boatdrill.UI.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    // A PasswordBox cannot be bound, by design; its text goes straight to the view model and the box is cleared.
    private async void AddTool_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel) return;

        var password = ToolPassword.Password;
        ToolPassword.Clear();
        try
        {
            await viewModel.AddToolAsync(password);
        }
        catch (Exception ex)
        {
            Log.Error("Could not add the repository or remote.", ex);
        }
    }

    private void CancelTool_Click(object sender, RoutedEventArgs e)
    {
        ToolPassword.Clear();
        (DataContext as SettingsViewModel)?.CancelTool();
    }
}
