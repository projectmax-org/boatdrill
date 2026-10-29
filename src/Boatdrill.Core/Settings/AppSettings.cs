using System.Text.Json.Serialization;
using Boatdrill.Core.Sampling;

namespace Boatdrill.Core.Settings;

public enum DrillFrequency
{
    Off,
    Daily,
    Weekly,
    Monthly,
}

public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>
/// Everything the user chooses: what to protect, where the copies are, when to drill. Plain settable
/// properties so it round-trips through JSON; <see cref="SettingsStore"/> keeps the values in range.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Left out of every source by default: churn that no backup is expected to keep.</summary>
    public static readonly IReadOnlyList<string> DefaultExcludes = ["Thumbs.db", "desktop.ini", "*.tmp", "~$*", "node_modules"];

    public int SchemaVersion { get; set; } = 1;

    public List<SourceSetting> Sources { get; set; } = [];

    public List<DestinationSetting> Destinations { get; set; } = [];

    public DrillFrequency Frequency { get; set; } = DrillFrequency.Weekly;

    /// <summary>On a laptop, wait for mains power before a scheduled drill.</summary>
    public bool OnlyOnMainsPower { get; set; } = true;

    /// <summary>Wait until the keyboard and mouse have been left alone for a few minutes.</summary>
    public bool OnlyWhenAway { get; set; } = true;

    /// <summary>A notification when a drill finds a destination that would not restore.</summary>
    public bool NotifyOnProblems { get; set; } = true;

    public int SampleSize { get; set; } = 200;

    public int ByteCapMb { get; set; } = 512;

    public bool StartWithWindows { get; set; } = true;

    /// <summary>Closing the window leaves Boatdrill in the notification area, where scheduled drills run.</summary>
    public bool KeepRunningInTray { get; set; } = true;

    public AppTheme Theme { get; set; } = AppTheme.System;

    public double WindowWidth { get; set; } = 1080;

    public double WindowHeight { get; set; } = 740;

    public bool HasCompletedFirstRun { get; set; }

    public bool HasShownTrayHint { get; set; }

    /// <summary>A drill needs something to protect and somewhere to look.</summary>
    [JsonIgnore]
    public bool IsReadyToDrill => Sources.Count > 0 && Destinations.Count > 0;

    public SampleOptions ToSampleOptions() => new()
    {
        SampleSize = SampleSize,
        ByteCap = ByteCapMb * 1024L * 1024,
    };

    /// <summary>The folders most people mean by "my files", those that exist. Destinations are theirs to add.</summary>
    public static AppSettings CreateDefault(IEnumerable<string> folders)
    {
        var settings = new AppSettings();
        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) continue;
            settings.Sources.Add(SourceSetting.ForFolder(folder));
        }

        return settings;
    }
}

public sealed class SourceSetting
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Folder { get; set; } = string.Empty;

    public List<string> Excludes { get; set; } = [];

    public static SourceSetting ForFolder(string folder)
    {
        var source = Source.FromPath(folder);
        return new SourceSetting
        {
            Id = source.Id,
            Name = source.Name,
            Folder = source.RootPath,
            Excludes = [.. AppSettings.DefaultExcludes],
        };
    }

    public Source ToSource() => new(Id, Name, Folder) { Excludes = [.. Excludes] };
}

public sealed class DestinationSetting
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Folder { get; set; } = string.Empty;

    public MirrorLayout Layout { get; set; } = MirrorLayout.Auto;

    public static DestinationSetting ForFolder(string folder, string? name = null)
    {
        var destination = Destination.MirrorFolder(folder, name);
        return new DestinationSetting
        {
            Id = destination.Id,
            Name = destination.Name,
            Folder = destination.RootPath,
            Layout = destination.Layout,
        };
    }

    public Destination ToDestination() => new(Id, Name, DestinationKind.MirrorFolder, Folder) { Layout = Layout };
}
