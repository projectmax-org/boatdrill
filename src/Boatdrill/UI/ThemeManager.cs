using System.Windows;
using Boatdrill.Core.Settings;
using Boatdrill.Platform;
using Microsoft.Win32;

namespace Boatdrill.UI;

/// <summary>
/// Applies the light or dark palette.
/// <para>
/// The palette dictionary sits at a known index in the application's merged dictionaries and is swapped
/// whole. Every style refers to brushes with DynamicResource, so the window re-colours without being rebuilt.
/// </para>
/// </summary>
public static class ThemeManager
{
    private const int PaletteIndex = 0;

    private static readonly Uri DarkPalette = new("pack://application:,,,/UI/Theme/Palette.Dark.xaml", UriKind.Absolute);
    private static readonly Uri LightPalette = new("pack://application:,,,/UI/Theme/Palette.Light.xaml", UriKind.Absolute);

    private static AppTheme _requested = AppTheme.System;
    private static bool _watchingSystem;

    /// <summary>True when the palette in use is the dark one.</summary>
    public static bool IsDark { get; private set; } = true;

    public static event EventHandler? ThemeChanged;

    public static void Apply(AppTheme theme)
    {
        _requested = theme;
        ApplyResolved(theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => IsSystemDark(),
        });
        WatchSystemTheme();
    }

    private static void ApplyResolved(bool dark)
    {
        var app = Application.Current;
        if (app is null) return;

        var palette = new ResourceDictionary { Source = dark ? DarkPalette : LightPalette };
        if (app.Resources.MergedDictionaries.Count > PaletteIndex)
            app.Resources.MergedDictionaries[PaletteIndex] = palette;
        else
            app.Resources.MergedDictionaries.Insert(PaletteIndex, palette);

        IsDark = dark;
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Reads the Windows "choose your app mode" preference.</summary>
    public static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the system theme preference.", ex);
            return false;
        }
    }

    private static void WatchSystemTheme()
    {
        if (_watchingSystem) return;
        _watchingSystem = true;

        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category != UserPreferenceCategory.General || _requested != AppTheme.System) return;
            Application.Current?.Dispatcher.BeginInvoke(new Action(() => ApplyResolved(IsSystemDark())));
        };
    }
}
