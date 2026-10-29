using System.Collections.ObjectModel;
using System.Reflection;
using Boatdrill.Core;
using Boatdrill.Core.Adapters;
using Boatdrill.Core.Settings;
using Boatdrill.Platform;

namespace Boatdrill.UI.ViewModels;

/// <summary>A value with the words that describe it in a drop-down.</summary>
public sealed record Choice<T>(string Label, T Value);

/// <summary>A protected folder in the Settings list.</summary>
public sealed class SourceRowViewModel(SourceSetting source)
{
    public string Id { get; } = source.Id;

    public string Name { get; } = source.Name;

    public string Folder { get; } = source.Folder;

    public string Excludes { get; } = source.Excludes.Count == 0 ? "Every file is checked." : "Left out: " + string.Join(", ", source.Excludes);
}

/// <summary>A destination in the Settings list. Its name, and a folder's layout, are edited in place.</summary>
public sealed class DestinationRowViewModel : ViewModelBase
{
    public static readonly IReadOnlyList<Choice<MirrorLayout>> Layouts =
    [
        new("Work it out from the files", MirrorLayout.Auto),
        new("The folder's contents, directly", MirrorLayout.Contents),
        new("A folder named after each source", MirrorLayout.RootFolder),
        new("The full path, as in E:\\Backup\\C\\Users", MirrorLayout.FullPath),
    ];

    private readonly SettingsStore _settings;
    private string _name;
    private Choice<MirrorLayout> _layout;

    public DestinationRowViewModel(SettingsStore settings, DestinationSetting destination)
    {
        _settings = settings;
        Id = destination.Id;
        Kind = destination.Kind;
        Folder = destination.Kind switch
        {
            DestinationKind.FileHistory => "File History on " + destination.Folder,
            DestinationKind.Restic => "restic repository at " + Destination.Redact(destination.Folder),
            _ => destination.Folder,
        };
        PasswordNote = destination.Kind != DestinationKind.Restic ? string.Empty
            : Dpapi.Unprotect(destination.Secret) is null ? "No password is saved that this Windows account can read. Add the repository again with its password."
            : "Its password is saved, encrypted so that only your Windows account can read it. Add the repository again to change it.";
        PasswordMissing = destination.Kind == DestinationKind.Restic && Dpapi.Unprotect(destination.Secret) is null;
        _name = destination.Name;
        _layout = Layouts.FirstOrDefault(l => l.Value == destination.Layout) ?? Layouts[0];
    }

    public string Id { get; }

    public DestinationKind Kind { get; }

    public string Folder { get; }

    /// <summary>Only a folder of plain copies has a layout to choose.</summary>
    public bool HasLayout => Kind == DestinationKind.MirrorFolder;

    /// <summary>For a repository, what is known about its password.</summary>
    public string PasswordNote { get; }

    public bool PasswordMissing { get; }

    public IReadOnlyList<Choice<MirrorLayout>> LayoutChoices => Layouts;

    public string Name
    {
        get => _name;
        set
        {
            var name = string.IsNullOrWhiteSpace(value) ? _name : value.Trim();
            if (!Set(ref _name, name)) return;
            _settings.Update(s =>
            {
                if (s.Destinations.FirstOrDefault(d => d.Id == Id) is { } d) d.Name = name;
            });
        }
    }

    public Choice<MirrorLayout> Layout
    {
        get => _layout;
        set
        {
            if (value is null || !Set(ref _layout, value)) return;
            _settings.Update(s =>
            {
                if (s.Destinations.FirstOrDefault(d => d.Id == Id) is { } d) d.Layout = value.Value;
            });
        }
    }
}

/// <summary>Everything on the Settings page. Each change is saved as it is made.</summary>
public sealed class SettingsViewModel : ViewModelBase, IDisposable
{
    public const string RepositoryUrl = "https://github.com/projectmax-org/project_01";

    private readonly AppServices _services;
    private string _message = string.Empty;
    private bool _messageIsWarning;
    private bool _isAddingRestic;
    private bool _isTestingRestic;
    private string _newResticLocation = string.Empty;
    private string _newResticTool = string.Empty;

    public SettingsViewModel(AppServices services)
    {
        _services = services;

        SampleSizes = WithCurrent(
        [
            new("100 files: quickest", 100),
            new("200 files: recommended", 200),
            new("400 files: a tighter estimate", 400),
            new("1,000 files: thorough", 1000),
        ], Settings.SampleSize, n => $"{n:N0} files");

        ByteCaps = WithCurrent(
        [
            new("256 MB", 256),
            new("512 MB: recommended", 512),
            new("1 GB", 1024),
            new("4 GB", 4096),
        ], Settings.ByteCapMb, n => $"{n:N0} MB");

        AddSourceCommand = new RelayCommand(AddSource);
        AddDestinationCommand = new RelayCommand(AddDestination);
        AddFileHistoryCommand = new AsyncRelayCommand(AddFileHistoryAsync);
        StartResticCommand = new RelayCommand(() => IsAddingRestic = true);
        BrowseResticCommand = new RelayCommand(BrowseRestic);
        RemoveSourceCommand = new RelayCommand(p => RemoveSource(p as SourceRowViewModel));
        RemoveDestinationCommand = new RelayCommand(p => RemoveDestination(p as DestinationRowViewModel));
        OpenDataFolderCommand = new RelayCommand(() => Shell.OpenFolder(Paths.DataDirectory));
        OpenLogCommand = new RelayCommand(() => Shell.OpenFolder(Paths.LogFile, selectFile: true));
        OpenRepositoryCommand = new RelayCommand(() => Shell.OpenUrl(RepositoryUrl));

        Reload();
        _services.Settings.Changed += OnSettingsChanged;
    }

    private AppSettings Settings => _services.Settings.Settings;

    public ObservableCollection<SourceRowViewModel> Sources { get; } = [];

    public ObservableCollection<DestinationRowViewModel> Destinations { get; } = [];

    public RelayCommand AddSourceCommand { get; }

    public RelayCommand AddDestinationCommand { get; }

    public AsyncRelayCommand AddFileHistoryCommand { get; }

    public RelayCommand StartResticCommand { get; }

    public RelayCommand BrowseResticCommand { get; }

    public RelayCommand RemoveSourceCommand { get; }

    public RelayCommand RemoveDestinationCommand { get; }

    public RelayCommand OpenDataFolderCommand { get; }

    public RelayCommand OpenLogCommand { get; }

    public RelayCommand OpenRepositoryCommand { get; }

    /// <summary>What the last add or remove did, or why it did not.</summary>
    public string Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    public bool MessageIsWarning
    {
        get => _messageIsWarning;
        private set => Set(ref _messageIsWarning, value);
    }

    // ---------------- adding a restic repository ----------------

    /// <summary>The form for a repository is open.</summary>
    public bool IsAddingRestic
    {
        get => _isAddingRestic;
        set => Set(ref _isAddingRestic, value);
    }

    /// <summary>A repository just added is being opened, to say whether its password works.</summary>
    public bool IsTestingRestic
    {
        get => _isTestingRestic;
        private set => Set(ref _isTestingRestic, value);
    }

    public string NewResticLocation
    {
        get => _newResticLocation;
        set => Set(ref _newResticLocation, value ?? string.Empty);
    }

    public string NewResticTool
    {
        get => _newResticTool;
        set => Set(ref _newResticTool, value ?? string.Empty);
    }

    // ---------------- checks ----------------

    public bool RecheckForBitRot
    {
        get => Settings.RecheckForBitRot;
        set => Change(s => s.RecheckForBitRot = value);
    }

    public bool UseCanaries
    {
        get => Settings.UseCanaries;
        set => Change(s => s.UseCanaries = value);
    }

    // ---------------- when to drill ----------------

    public bool IsDaily
    {
        get => Settings.Frequency == DrillFrequency.Daily;
        set => SetFrequency(value, DrillFrequency.Daily);
    }

    public bool IsWeekly
    {
        get => Settings.Frequency == DrillFrequency.Weekly;
        set => SetFrequency(value, DrillFrequency.Weekly);
    }

    public bool IsMonthly
    {
        get => Settings.Frequency == DrillFrequency.Monthly;
        set => SetFrequency(value, DrillFrequency.Monthly);
    }

    public bool IsOff
    {
        get => Settings.Frequency == DrillFrequency.Off;
        set => SetFrequency(value, DrillFrequency.Off);
    }

    public bool OnlyOnMainsPower
    {
        get => Settings.OnlyOnMainsPower;
        set => Change(s => s.OnlyOnMainsPower = value);
    }

    public bool OnlyWhenAway
    {
        get => Settings.OnlyWhenAway;
        set => Change(s => s.OnlyWhenAway = value);
    }

    public bool NotifyOnProblems
    {
        get => Settings.NotifyOnProblems;
        set => Change(s => s.NotifyOnProblems = value);
    }

    // ---------------- how much to read ----------------

    public IReadOnlyList<Choice<int>> SampleSizes { get; }

    public Choice<int>? SelectedSampleSize
    {
        get => SampleSizes.FirstOrDefault(c => c.Value == Settings.SampleSize);
        set
        {
            if (value is not null) Change(s => s.SampleSize = value.Value);
        }
    }

    public IReadOnlyList<Choice<int>> ByteCaps { get; }

    public Choice<int>? SelectedByteCap
    {
        get => ByteCaps.FirstOrDefault(c => c.Value == Settings.ByteCapMb);
        set
        {
            if (value is not null) Change(s => s.ByteCapMb = value.Value);
        }
    }

    // ---------------- the app ----------------

    public bool StartWithWindows
    {
        get => Settings.StartWithWindows;
        set
        {
            var applied = StartupManager.SetEnabled(value) ? value : StartupManager.IsEnabled();
            Change(s => s.StartWithWindows = applied);
        }
    }

    public bool KeepRunningInTray
    {
        get => Settings.KeepRunningInTray;
        set => Change(s => s.KeepRunningInTray = value);
    }

    public bool IsSystemTheme
    {
        get => Settings.Theme == AppTheme.System;
        set => SetTheme(value, AppTheme.System);
    }

    public bool IsLightTheme
    {
        get => Settings.Theme == AppTheme.Light;
        set => SetTheme(value, AppTheme.Light);
    }

    public bool IsDarkTheme
    {
        get => Settings.Theme == AppTheme.Dark;
        set => SetTheme(value, AppTheme.Dark);
    }

    public string DataFolder => Paths.DataDirectory;

    public string Version => "Boatdrill " + (typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.2.0");

    // ---------------- folders ----------------

    private void AddSource()
    {
        var folder = Shell.PickFolder("Choose a folder to protect");
        if (folder is null) return;

        var source = SourceSetting.ForFolder(folder);
        if (Settings.Sources.Any(s => s.Id == source.Id))
        {
            Say($"{source.Name} is already protected.", warning: false);
            return;
        }

        if (Settings.Destinations.FirstOrDefault(d => SameOrInside(source.Folder, d.Folder)) is { } backup)
        {
            Say($"{source.Folder} is part of {backup.Name}, the backup itself. Choose the folder whose files it keeps.", warning: true);
            return;
        }

        _services.Settings.Update(s => s.Sources.Add(source));
        Say($"{source.Name} is protected from now on. Its files are in the next drill.", warning: false);
    }

    private void AddDestination()
    {
        var folder = Shell.PickFolder("Choose the folder or drive your backup goes to");
        if (folder is null) return;

        var destination = DestinationSetting.ForFolder(folder, Shell.DriveName(folder));
        if (Settings.Destinations.Any(d => d.Id == destination.Id))
        {
            Say($"{destination.Name} is already in the list.", warning: false);
            return;
        }

        if (Settings.Sources.FirstOrDefault(s => SameOrInside(destination.Folder, s.Folder)) is { } protectedFolder)
        {
            _services.Settings.Update(s => s.Destinations.Add(destination));
            Say($"{destination.Folder} is inside {protectedFolder.Name}, which Boatdrill protects. A backup on the same disk as the files it keeps would be lost with them.", warning: true);
            return;
        }

        _services.Settings.Update(s => s.Destinations.Add(destination));
        Say($"{destination.Name} is added. Boatdrill only ever reads from it.", warning: false);
    }

    private async Task AddFileHistoryAsync()
    {
        var folder = Shell.PickFolder("Choose the drive File History saves to, or its FileHistory folder");
        if (folder is null) return;

        var drive = Path.GetPathRoot(Path.GetFullPath(folder))?.TrimEnd('\\', '/');
        var destination = DestinationSetting.ForFileHistory(folder, string.IsNullOrEmpty(drive) ? "File History" : $"File History ({drive})");
        if (Settings.Destinations.Any(d => d.Id == destination.Id))
        {
            Say($"{destination.Name} is already in the list.", warning: false);
            return;
        }

        var probe = await Task.Run(() => new FileHistoryAdapter().ProbeAsync(destination.ToDestination(), CancellationToken.None));
        if (!probe.Reachable)
        {
            Say($"{probe.Detail}. Choose the drive that File History saves to, or the FileHistory folder on it.", warning: true);
            return;
        }

        _services.Settings.Update(s => s.Destinations.Add(destination));
        Say($"{destination.Name} is added. Each drill reads the newest version of every sampled file.", warning: false);
    }

    private void BrowseRestic()
    {
        if (Shell.PickFolder("Choose the folder that holds the restic repository") is { } folder) NewResticLocation = folder;
    }

    public void CancelRestic()
    {
        IsAddingRestic = false;
        NewResticLocation = string.Empty;
        NewResticTool = string.Empty;
    }

    /// <summary>
    /// Adds the repository in the form, or, when it is already in the list, replaces its password. Then opens it
    /// with restic to say whether the password works. The password is kept only encrypted (DPAPI).
    /// </summary>
    public async Task AddResticAsync(string password)
    {
        var location = NewResticLocation.Trim();
        if (location.Length == 0)
        {
            Say("Type where the repository is: a folder, or a URL such as sftp:nas:/srv/restic.", warning: true);
            return;
        }

        if (password.Length == 0)
        {
            Say("Type the repository's password. Boatdrill keeps it encrypted, for your Windows account only.", warning: true);
            return;
        }

        DestinationSetting destination;
        try
        {
            var tool = NewResticTool.Trim();
            destination = DestinationSetting.ForRestic(location, Dpapi.Protect(password), toolPath: tool.Length == 0 ? null : tool);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or InvalidOperationException)
        {
            Say("That location cannot be used: " + ex.Message, warning: true);
            return;
        }

        var existing = Settings.Destinations.FirstOrDefault(d => d.Id == destination.Id);
        _services.Settings.Update(s =>
        {
            if (s.Destinations.FirstOrDefault(d => d.Id == destination.Id) is { } known)
            {
                known.Secret = destination.Secret;
                known.ToolPath = destination.ToolPath;
            }
            else
            {
                s.Destinations.Add(destination);
            }
        });
        CancelRestic();
        var name = existing?.Name ?? destination.Name;
        Say(existing is null ? $"Opening {name} with restic…" : $"The password for {name} is replaced. Opening it with restic…", warning: false);

        IsTestingRestic = true;
        try
        {
            var probe = await Task.Run(() => new ResticAdapter().ProbeAsync(destination.ToDestination(_ => password), CancellationToken.None));
            Say(probe.Reachable
                    ? $"{name} is ready: {probe.Detail}. Boatdrill only ever reads from it."
                    : $"{name} is in the list, but restic could not open it: {probe.Detail}.",
                warning: !probe.Reachable);
        }
        finally
        {
            IsTestingRestic = false;
        }
    }

    private void RemoveSource(SourceRowViewModel? row)
    {
        if (row is null) return;
        _services.Settings.Update(s => s.Sources.RemoveAll(x => x.Id == row.Id));
        Say($"{row.Name} is no longer protected. Its files stay where they are.", warning: false);
    }

    private void RemoveDestination(DestinationRowViewModel? row)
    {
        if (row is null) return;
        _services.Settings.Update(s => s.Destinations.RemoveAll(x => x.Id == row.Id));
        Say($"{row.Name} is no longer checked. Nothing in it was touched.", warning: false);
    }

    private static bool SameOrInside(string folder, string container)
    {
        var a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        var b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(container)) + Path.DirectorySeparatorChar;
        return a.StartsWith(b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    // ---------------- plumbing ----------------

    private void SetFrequency(bool chosen, DrillFrequency frequency)
    {
        if (chosen && Settings.Frequency != frequency) Change(s => s.Frequency = frequency);
    }

    private void SetTheme(bool chosen, AppTheme theme)
    {
        if (!chosen || Settings.Theme == theme) return;
        Change(s => s.Theme = theme);
        ThemeManager.Apply(theme);
    }

    private void Change(Action<AppSettings> mutate) => _services.Settings.Update(mutate);

    private void Say(string message, bool warning)
    {
        Message = message;
        MessageIsWarning = warning;
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        // Changes arrive on the UI thread except when made elsewhere; the lists only change from here.
        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(Reload));
            return;
        }

        Reload();
    }

    /// <summary>Rebuilds the lists and re-reads every value, so the page always shows what is saved.</summary>
    private void Reload()
    {
        var settings = Settings;

        if (!Sources.Select(s => s.Id).SequenceEqual(settings.Sources.Select(s => s.Id)))
        {
            Sources.Clear();
            foreach (var source in settings.Sources) Sources.Add(new SourceRowViewModel(source));
        }

        if (!Destinations.Select(d => (d.Id, d.PasswordMissing)).SequenceEqual(settings.Destinations.Select(d => (d.Id, d.Kind == DestinationKind.Restic && Dpapi.Unprotect(d.Secret) is null))))
        {
            Destinations.Clear();
            foreach (var destination in settings.Destinations) Destinations.Add(new DestinationRowViewModel(_services.Settings, destination));
        }

        Raise(nameof(IsDaily), nameof(IsWeekly), nameof(IsMonthly), nameof(IsOff),
            nameof(OnlyOnMainsPower), nameof(OnlyWhenAway), nameof(NotifyOnProblems),
            nameof(RecheckForBitRot), nameof(UseCanaries),
            nameof(SelectedSampleSize), nameof(SelectedByteCap),
            nameof(StartWithWindows), nameof(KeepRunningInTray),
            nameof(IsSystemTheme), nameof(IsLightTheme), nameof(IsDarkTheme));
    }

    /// <summary>A value set by hand in settings.json gets its own entry, so the drop-down never shows blank.</summary>
    private static IReadOnlyList<Choice<int>> WithCurrent(List<Choice<int>> choices, int current, Func<int, string> label)
    {
        if (choices.All(c => c.Value != current)) choices.Add(new Choice<int>(label(current), current));
        return choices.OrderBy(c => c.Value).ToList();
    }

    public void Dispose() => _services.Settings.Changed -= OnSettingsChanged;
}
