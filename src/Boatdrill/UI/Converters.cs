using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Boatdrill.UI;

/// <summary>true, a non-empty string or a non-empty collection becomes Visible; everything else Collapsed. Invert with "invert".</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value switch
        {
            bool b => b,
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            int i => i != 0,
            System.Collections.ICollection c => c.Count > 0,
            _ => true,
        };

        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase)) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is Visibility.Visible;
}

/// <summary>The icon for a kind of destination: a drive, File History's versions, a repository, a cloud, a box of archives.</summary>
public sealed class DestinationIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Application.Current?.TryFindResource(value switch
        {
            Core.DestinationKind.FileHistory => "Icon.Versions",
            Core.DestinationKind.Restic => "Icon.Repository",
            Core.DestinationKind.Rclone => "Icon.Cloud",
            Core.DestinationKind.Archive => "Icon.Archive",
            _ => "Icon.Drive",
        });

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}
