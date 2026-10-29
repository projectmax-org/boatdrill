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
    private async void AddRestic_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel) return;

        var password = ResticPassword.Password;
        ResticPassword.Clear();
        try
        {
            await viewModel.AddResticAsync(password);
        }
        catch (Exception ex)
        {
            Log.Error("Could not add the repository.", ex);
        }
    }

    private void CancelRestic_Click(object sender, RoutedEventArgs e)
    {
        ResticPassword.Clear();
        (DataContext as SettingsViewModel)?.CancelRestic();
    }
}
