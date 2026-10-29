using System.Diagnostics;

namespace Boatdrill.Platform;

/// <summary>Small wrappers around shell operations, each one non-throwing.</summary>
public static class Shell
{
    /// <summary>Opens an https URL in the default browser. Anything else is ignored.</summary>
    public static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("Could not open " + url, ex);
        }
    }

    /// <summary>Opens a folder in File Explorer, or selects a file in its folder.</summary>
    public static void OpenFolder(string? path, bool selectFile = false)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            if (selectFile && File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("Could not open " + path, ex);
        }
    }

    /// <summary>Shows the folder picker. Returns null when the user cancels.</summary>
    public static string? PickFolder(string title)
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title, Multiselect = false };
            return dialog.ShowDialog() == true ? dialog.FolderName : null;
        }
        catch (Exception ex)
        {
            Log.Warn("Folder picker failed.", ex);
            return null;
        }
    }

    /// <summary>"Backup (E:)" for the root of a labelled drive, "Drive E:" without a label, null for any other folder.</summary>
    public static string? DriveName(string folder)
    {
        try
        {
            var full = Path.GetFullPath(folder);
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root) || !string.Equals(Path.TrimEndingDirectorySeparator(full), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
                return null;

            var letter = root.TrimEnd('\\', '/');
            var drive = new DriveInfo(root);
            var label = drive.IsReady ? drive.VolumeLabel : string.Empty;
            return string.IsNullOrWhiteSpace(label) ? $"Drive {letter}" : $"{label} ({letter})";
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
