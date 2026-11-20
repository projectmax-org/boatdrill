using System.Collections.ObjectModel;
using System.Reflection;
using Boatdrill.Core;
using Boatdrill.Core.Adapters;
using Boatdrill.Core.Settings;
using Boatdrill.Core.Text;
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

    public string Excludes { get; } = source.Excludes.Count == 0
        ? Language.Pick("Every file is checked.", "모든 파일을 확인합니다.")
        : Language.Pick("Left out: ", "제외: ") + string.Join(", ", source.Excludes);
}

/// <summary>A destination in the Settings list. Its name, and a folder's layout, are edited in place.</summary>
public sealed class DestinationRowViewModel : ViewModelBase
{
    private readonly IReadOnlyList<Choice<MirrorLayout>> _layouts =
    [
        new(Language.Pick("Work it out from the files", "파일을 보고 알아내기"), MirrorLayout.Auto),
        new(Language.Pick("The folder's contents, directly", "보호하는 폴더의 내용을 백업 폴더에 바로 저장"), MirrorLayout.Contents),
        new(Language.Pick("A folder named after each source", "보호하는 폴더마다 같은 이름의 폴더에 저장"), MirrorLayout.RootFolder),
        new(Language.Pick("The full path, as in E:\\Backup\\C\\Users", "전체 경로대로 저장 (예: E:\\Backup\\C\\Users)"), MirrorLayout.FullPath),
    ];

    private readonly SettingsStore _settings;
    private string _name;
    private Choice<MirrorLayout> _layout;

    public DestinationRowViewModel(SettingsStore settings, DestinationSetting destination)
    {
        _settings = settings;
        Id = destination.Id;
        Kind = destination.Kind;
        Folder = Destination.Describe(destination.Kind, destination.Folder);
        PasswordMissing = IsPasswordMissing(destination);
        PasswordNote = destination.Kind switch
        {
            DestinationKind.Restic or DestinationKind.Kopia or DestinationKind.Rclone when PasswordMissing => Language.Pick(
                "No password is saved that this Windows account can read. Add it again with its password.",
                "이 Windows 계정으로 읽을 수 있는 암호가 저장되어 있지 않습니다. 암호와 함께 다시 추가하세요."),
            DestinationKind.Restic or DestinationKind.Kopia => Language.Pick(
                "Its password is saved, encrypted so that only your Windows account can read it. Add the repository again to change it.",
                "암호는 사용자의 Windows 계정만 읽을 수 있도록 암호화해 저장했습니다. 바꾸려면 저장소를 다시 추가하세요."),
            DestinationKind.Rclone when destination.Secret is not null => Language.Pick(
                "Its config password is saved, encrypted so that only your Windows account can read it. Add the remote again to change it.",
                "설정 파일 암호는 사용자의 Windows 계정만 읽을 수 있도록 암호화해 저장했습니다. 바꾸려면 원격 저장소를 다시 추가하세요."),
            DestinationKind.Rclone => Language.Pick("Read with your rclone config as it is.", "지금의 rclone 설정 그대로 읽습니다."),
            _ => string.Empty,
        };
        _name = destination.Name;
        _layout = _layouts.FirstOrDefault(l => l.Value == destination.Layout) ?? _layouts[0];
    }

    /// <summary>A repository with no password, or a password this Windows account cannot decrypt (settings from elsewhere).</summary>
    public static bool IsPasswordMissing(DestinationSetting destination) => destination.Kind switch
    {
        DestinationKind.Restic or DestinationKind.Kopia => Dpapi.Unprotect(destination.Secret) is null,
        DestinationKind.Rclone => destination.Secret is not null && Dpapi.Unprotect(destination.Secret) is null,
        _ => false,
    };

    public string Id { get; }

    public DestinationKind Kind { get; }

    public string Folder { get; }

    /// <summary>Only a folder of plain copies has a layout to choose.</summary>
    public bool HasLayout => Kind == DestinationKind.MirrorFolder;

    /// <summary>For a repository, what is known about its password.</summary>
    public string PasswordNote { get; }

    public bool PasswordMissing { get; }

    public IReadOnlyList<Choice<MirrorLayout>> LayoutChoices => _layouts;

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

/// <summary>
/// The words of the form for a backup read through a tool, for one kind. <see cref="ToolWith"/> is the tool's name
/// with 으로 or 로 after it, for Korean sentences: restic (레스틱) and rclone (알클론) end in a consonant, kopia (코피아)
/// in a vowel.
/// </summary>
public sealed record ToolForm(
    string Title,
    string Intro,
    string LocationLabel,
    string LocationHint,
    string PasswordLabel,
    string PasswordHint,
    bool PasswordRequired,
    string ToolLabel,
    string ToolHint,
    string AddText,
    bool CanBrowse,
    string Tool,
    string Noun,
    string ToolWith);

/// <summary>Everything on the Settings page. Each change is saved as it is made.</summary>
public sealed class SettingsViewModel : ViewModelBase, IDisposable
{
    public const string RepositoryUrl = "https://github.com/projectmax-org/boatdrill";

    private readonly AppServices _services;
    private string _message = string.Empty;
    private bool _messageIsWarning;
    private DestinationKind? _addingKind;
    private bool _isTestingTool;
    private string _newToolLocation = string.Empty;
    private string _newToolPath = string.Empty;

    private readonly IReadOnlyDictionary<DestinationKind, ToolForm> _forms = new Dictionary<DestinationKind, ToolForm>
    {
        [DestinationKind.Restic] = new(
            Language.Pick("A restic repository", "restic 저장소"),
            Language.Pick(
                "Boatdrill reads it with restic, which has to be installed. Each drill restores a sample of files into Boatdrill's own folder; nothing is written to the repository, not even a lock.",
                "Boatdrill은 restic으로 이 저장소를 읽으므로 restic이 설치되어 있어야 합니다. 복원 훈련마다 파일 표본을 Boatdrill의 작업 폴더에 복원하며, 저장소에는 잠금 파일조차 쓰지 않습니다."),
            Language.Pick("Where it is", "위치"), Language.Pick("E:\\restic, or sftp:nas:/srv/restic", "E:\\restic 또는 sftp:nas:/srv/restic"),
            Language.Pick("Password", "암호"), string.Empty, PasswordRequired: true,
            Language.Pick("restic itself", "restic 프로그램"), Language.Pick("Leave empty when restic is on the PATH", "restic이 PATH에 있으면 비워 두세요"),
            Language.Pick("Add the repository", "저장소 추가"), CanBrowse: true, "restic", Language.Pick("repository", "저장소"), "restic으로"),
        [DestinationKind.Rclone] = new(
            Language.Pick("An rclone remote", "rclone 원격 저장소"),
            Language.Pick(
                "Boatdrill reads it with rclone, which has to be installed with the remote set up in its config. Each drill downloads a sample of files into Boatdrill's own folder; nothing is written to the remote.",
                "Boatdrill은 rclone으로 이 원격 저장소를 읽으므로, rclone이 설치되어 있고 설정에 원격 저장소가 등록되어 있어야 합니다. 복원 훈련마다 파일 표본을 Boatdrill의 작업 폴더로 내려받으며, 원격 저장소에는 아무것도 쓰지 않습니다."),
            Language.Pick("Remote and path", "원격 저장소와 경로"), "gdrive:Backups/Laptop",
            Language.Pick("Config password", "설정 파일 암호"), Language.Pick("Only if your rclone config is encrypted", "rclone 설정 파일이 암호화되어 있을 때만"), PasswordRequired: false,
            Language.Pick("rclone itself", "rclone 프로그램"), Language.Pick("Leave empty when rclone is on the PATH", "rclone이 PATH에 있으면 비워 두세요"),
            Language.Pick("Add the remote", "원격 저장소 추가"), CanBrowse: false, "rclone", Language.Pick("remote", "원격 저장소"), "rclone으로"),
        [DestinationKind.Kopia] = new(
            Language.Pick("A Kopia repository", "Kopia 저장소"),
            Language.Pick(
                "Boatdrill reads it with kopia, which KopiaUI installs with itself. Give the repository's folder, or, for a repository in the cloud, the .config file of a connection KopiaUI already has. Boatdrill makes a read-only connection of its own: yours, its cache and its saved password are left alone, and nothing is written to the repository.",
                "Boatdrill은 kopia로 이 저장소를 읽습니다. kopia는 KopiaUI를 설치하면 함께 설치됩니다. 저장소 폴더를 적거나, 클라우드에 있는 저장소라면 KopiaUI가 이미 쓰고 있는 연결의 .config 파일을 적으세요. Boatdrill은 따로 읽기 전용 연결을 만들기 때문에 사용자의 연결과 캐시, 저장된 암호는 그대로 두며, 저장소에는 아무것도 쓰지 않습니다."),
            Language.Pick("Where it is", "위치"), Language.Pick("E:\\Kopia, or …\\kopia\\repository.config", "E:\\Kopia 또는 …\\kopia\\repository.config"),
            Language.Pick("Password", "암호"), string.Empty, PasswordRequired: true,
            Language.Pick("kopia itself", "kopia 프로그램"), Language.Pick("Leave empty when kopia is on the PATH or KopiaUI is installed", "kopia가 PATH에 있거나 KopiaUI가 설치되어 있으면 비워 두세요"),
            Language.Pick("Add the repository", "저장소 추가"), CanBrowse: true, "kopia", Language.Pick("repository", "저장소"), "kopia로"),
    };

    public SettingsViewModel(AppServices services)
    {
        _services = services;

        SampleSizes = WithCurrent(
        [
            new(Language.Pick("100 files: quickest", "파일 100개: 가장 빠름"), 100),
            new(Language.Pick("200 files: recommended", "파일 200개: 권장"), 200),
            new(Language.Pick("400 files: a tighter estimate", "파일 400개: 더 정확한 추정"), 400),
            new(Language.Pick("1,000 files: thorough", "파일 1,000개: 꼼꼼하게"), 1000),
        ], Settings.SampleSize, n => Humanize.Count(n, "file", "파일"));

        ByteCaps = WithCurrent(
        [
            new("256 MB", 256),
            new(Language.Pick("512 MB: recommended", "512 MB: 권장"), 512),
            new("1 GB", 1024),
            new("4 GB", 4096),
        ], Settings.ByteCapMb, n => $"{n:N0} MB");

        AddSourceCommand = new RelayCommand(AddSource);
        AddDestinationCommand = new RelayCommand(AddDestination);
        AddFileHistoryCommand = new AsyncRelayCommand(AddFileHistoryAsync);
        AddArchivesCommand = new AsyncRelayCommand(AddArchivesAsync);
        StartResticCommand = new RelayCommand(() => AddingKind = DestinationKind.Restic, () => AddingKind is null);
        StartRcloneCommand = new RelayCommand(() => AddingKind = DestinationKind.Rclone, () => AddingKind is null);
        StartKopiaCommand = new RelayCommand(StartKopia, () => AddingKind is null);
        BrowseToolCommand = new RelayCommand(BrowseTool);
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

    public AsyncRelayCommand AddArchivesCommand { get; }

    public RelayCommand StartResticCommand { get; }

    public RelayCommand StartRcloneCommand { get; }

    public RelayCommand StartKopiaCommand { get; }

    public RelayCommand BrowseToolCommand { get; }

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

    // ---------------- adding a repository or a remote ----------------

    /// <summary>The kind of backup whose form is open, or null when it is closed.</summary>
    public DestinationKind? AddingKind
    {
        get => _addingKind;
        set
        {
            if (!Set(ref _addingKind, value)) return;
            Raise(nameof(IsAddingTool), nameof(Form));
            StartResticCommand.RaiseCanExecuteChanged();
            StartRcloneCommand.RaiseCanExecuteChanged();
            StartKopiaCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsAddingTool => AddingKind is not null;

    /// <summary>The open form's words.</summary>
    public ToolForm? Form => AddingKind is { } kind ? _forms[kind] : null;

    /// <summary>A repository or remote just added is being opened, to say whether it works.</summary>
    public bool IsTestingTool
    {
        get => _isTestingTool;
        private set => Set(ref _isTestingTool, value);
    }

    public string NewToolLocation
    {
        get => _newToolLocation;
        set => Set(ref _newToolLocation, value ?? string.Empty);
    }

    public string NewToolPath
    {
        get => _newToolPath;
        set => Set(ref _newToolPath, value ?? string.Empty);
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

    public string Version => "Boatdrill " + (typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3));

    // ---------------- folders ----------------

    private void AddSource()
    {
        var folder = Shell.PickFolder(Language.Pick("Choose a folder to protect", "보호할 폴더 고르기"));
        if (folder is null) return;

        var source = SourceSetting.ForFolder(folder);

        // Named as Explorer names it: Documents is "문서" on Korean Windows. The backups are still matched by
        // the folder's own name on disk.
        if (Shell.FolderDisplayName(source.Folder) is { } shown) source.Name = shown;
        if (Settings.Sources.Any(s => s.Id == source.Id))
        {
            Say(Language.Pick($"{source.Name} is already protected.", $"{Korean.Topic(Korean.Folder(source.Name))} 이미 보호하고 있습니다."), warning: false);
            return;
        }

        if (Settings.Destinations.FirstOrDefault(d => SameOrInside(source.Folder, d.Folder)) is { } backup)
        {
            Say(Language.Pick(
                $"{source.Folder} is part of {backup.Name}, the backup itself. Choose the folder whose files it keeps.",
                $"{Korean.Topic(Korean.Folder(source.Folder))} 백업인 {backup.Name}의 일부입니다. 백업이 보관하는 원래 파일이 있는 폴더를 고르세요."), warning: true);
            return;
        }

        _services.Settings.Update(s => s.Sources.Add(source));
        Say(Language.Pick($"{source.Name} is protected from now on. Its files are in the next drill.", $"이제부터 {Korean.Object(Korean.Folder(source.Name))} 보호합니다. 다음 복원 훈련부터 이 폴더의 파일도 확인합니다."), warning: false);
    }

    private void AddDestination()
    {
        var folder = Shell.PickFolder(Language.Pick("Choose the folder or drive your backup goes to", "백업이 저장되는 폴더나 드라이브 고르기"));
        if (folder is null) return;

        var destination = DestinationSetting.ForFolder(folder, Shell.DriveName(folder));
        if (Settings.Destinations.Any(d => d.Id == destination.Id))
        {
            Say(AlreadyListed(destination.Name), warning: false);
            return;
        }

        if (Settings.Sources.FirstOrDefault(s => SameOrInside(destination.Folder, s.Folder)) is { } protectedFolder)
        {
            _services.Settings.Update(s => s.Destinations.Add(destination));
            Say(InsideProtected(destination.Folder, protectedFolder.Name), warning: true);
            return;
        }

        _services.Settings.Update(s => s.Destinations.Add(destination));
        Say(Language.Pick($"{destination.Name} is added. Boatdrill only ever reads from it.", $"{Korean.Object(destination.Name)} 추가했습니다. Boatdrill은 이곳을 읽기만 합니다."), warning: false);
    }

    private async Task AddFileHistoryAsync()
    {
        var folder = Shell.PickFolder(Language.Pick("Choose the drive File History saves to, or its FileHistory folder", "파일 히스토리가 저장되는 드라이브나 그 안의 FileHistory 폴더 고르기"));
        if (folder is null) return;

        var drive = Path.GetPathRoot(Path.GetFullPath(folder))?.TrimEnd('\\', '/');
        var fileHistory = Language.Pick("File History", "파일 히스토리");
        var destination = DestinationSetting.ForFileHistory(folder, string.IsNullOrEmpty(drive) ? fileHistory : $"{fileHistory} ({drive})");
        if (Settings.Destinations.Any(d => d.Id == destination.Id))
        {
            Say(AlreadyListed(destination.Name), warning: false);
            return;
        }

        var probe = await Task.Run(() => new FileHistoryAdapter().ProbeAsync(destination.ToDestination(), CancellationToken.None));
        if (!probe.Reachable)
        {
            Say(probe.Detail + Language.Pick(". Choose the drive that File History saves to, or the FileHistory folder on it.", ". 파일 히스토리가 저장되는 드라이브나 그 안의 FileHistory 폴더를 고르세요."), warning: true);
            return;
        }

        _services.Settings.Update(s => s.Destinations.Add(destination));
        Say(Language.Pick($"{destination.Name} is added. Each drill reads the newest version of every sampled file.", $"{Korean.Object(destination.Name)} 추가했습니다. 복원 훈련마다 표본 파일의 가장 최근 버전을 읽습니다."), warning: false);
    }

    private async Task AddArchivesAsync()
    {
        var folder = Shell.PickFolder(Language.Pick("Choose the folder your zip or 7z backups are saved in", "zip이나 7z 백업이 저장되는 폴더 고르기"));
        if (folder is null) return;

        var destination = DestinationSetting.ForArchives(folder);
        if (Settings.Destinations.Any(d => d.Id == destination.Id))
        {
            Say(AlreadyListed(destination.Name), warning: false);
            return;
        }

        var probe = await Task.Run(() => new ArchiveAdapter().ProbeAsync(destination.ToDestination(), CancellationToken.None));
        if (!probe.Reachable)
        {
            Say(probe.Detail + Language.Pick(". Choose the folder your backup program saves its zip or 7z archives in.", ". 백업 프로그램이 zip이나 7z 압축 파일을 저장하는 폴더를 고르세요."), warning: true);
            return;
        }

        _services.Settings.Update(s => s.Destinations.Add(destination));
        if (Settings.Sources.FirstOrDefault(s => SameOrInside(destination.Folder, s.Folder)) is { } protectedFolder)
        {
            Say(InsideProtected(destination.Folder, protectedFolder.Name), warning: true);
            return;
        }

        Say(Language.Pick(
            $"{destination.Name} is added: {probe.Detail}. Each drill reads the newest archive that holds each protected folder.",
            $"{Korean.Object(destination.Name)} 추가했습니다: {probe.Detail}. 복원 훈련마다 보호하는 폴더가 담긴 가장 최근 압축 파일을 읽습니다."), warning: false);
    }

    /// <summary>Opens the Kopia form with KopiaUI's own connection filled in when there is one, so only the password is left to type.</summary>
    private void StartKopia()
    {
        AddingKind = DestinationKind.Kopia;
        if (NewToolLocation.Length == 0 && KopiaAdapter.DefaultConnection() is { } connection) NewToolLocation = connection;
    }

    private void BrowseTool()
    {
        if (Shell.PickFolder(Language.Pick("Choose the folder that holds the repository", "저장소가 있는 폴더 고르기")) is { } folder) NewToolLocation = folder;
    }

    public void CancelTool()
    {
        AddingKind = null;
        NewToolLocation = string.Empty;
        NewToolPath = string.Empty;
    }

    /// <summary>
    /// Adds the repository or remote in the form, or, when it is already in the list, replaces its password. Then
    /// opens it with its tool to say whether it works. A password is kept only encrypted (DPAPI).
    /// </summary>
    public async Task AddToolAsync(string password)
    {
        if (AddingKind is not { } kind || Form is not { } form) return;

        var location = NewToolLocation.Trim();
        if (location.Length == 0)
        {
            Say(Language.Pick($"Type where the {form.Noun} is, such as {form.LocationHint}.", $"{form.Noun} 위치를 입력하세요. 예: {form.LocationHint}"), warning: true);
            return;
        }

        if (form.PasswordRequired && password.Length == 0)
        {
            Say(Language.Pick($"Type the {form.Noun}'s password. Boatdrill keeps it encrypted, for your Windows account only.", $"{form.Noun} 암호를 입력하세요. Boatdrill은 사용자의 Windows 계정만 읽을 수 있도록 암호화해 보관합니다."), warning: true);
            return;
        }

        DestinationSetting destination;
        try
        {
            var tool = NewToolPath.Trim();
            var stored = password.Length == 0 ? null : Dpapi.Protect(password);
            destination = kind switch
            {
                DestinationKind.Rclone => DestinationSetting.ForRclone(location, stored, toolPath: tool.Length == 0 ? null : tool),
                DestinationKind.Kopia => DestinationSetting.ForKopia(location, stored, toolPath: tool.Length == 0 ? null : tool),
                _ => DestinationSetting.ForRestic(location, stored, toolPath: tool.Length == 0 ? null : tool),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or InvalidOperationException)
        {
            Say(Language.Pick("That location cannot be used: ", "그 위치는 쓸 수 없습니다: ") + ex.Message, warning: true);
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
        CancelTool();
        var name = existing?.Name ?? destination.Name;
        Say((existing is null, Language.IsKorean) switch
        {
            (true, false) => $"Opening {name} with {form.Tool}…",
            (true, true) => $"{form.ToolWith} {Korean.Object(name)} 여는 중…",
            (false, false) => $"{name} is updated. Opening it with {form.Tool}…",
            (false, true) => $"{Korean.Object(name)} 바꿨습니다. {form.ToolWith} 여는 중…",
        }, warning: false);

        IsTestingTool = true;
        try
        {
            var target = destination.ToDestination(_ => password.Length == 0 ? null : password);
            IDestinationAdapter adapter = kind switch
            {
                DestinationKind.Rclone => new RcloneAdapter(),
                DestinationKind.Kopia => new KopiaAdapter(),
                _ => new ResticAdapter(),
            };
            var probe = await Task.Run(() => adapter.ProbeAsync(target, CancellationToken.None));
            Say(probe.Reachable
                    ? Language.Pick($"{name} is ready: {probe.Detail}. Boatdrill only ever reads from it.", $"{name} 준비 완료: {probe.Detail}. Boatdrill은 이곳을 읽기만 합니다.")
                    : Language.Pick($"{name} is in the list, but {form.Tool} could not open it: {probe.Detail}.", $"{Korean.Object(name)} 목록에 넣었지만 {form.ToolWith} 열지 못했습니다: {probe.Detail}."),
                warning: !probe.Reachable);
        }
        finally
        {
            IsTestingTool = false;
        }
    }

    private void RemoveSource(SourceRowViewModel? row)
    {
        if (row is null) return;
        _services.Settings.Update(s => s.Sources.RemoveAll(x => x.Id == row.Id));
        Say(Language.Pick($"{row.Name} is no longer protected. Its files stay where they are.", $"{Korean.Topic(Korean.Folder(row.Name))} 더 이상 보호하지 않습니다. 폴더의 파일은 그대로 있습니다."), warning: false);
    }

    private void RemoveDestination(DestinationRowViewModel? row)
    {
        if (row is null) return;
        _services.Settings.Update(s => s.Destinations.RemoveAll(x => x.Id == row.Id));
        Say(Language.Pick($"{row.Name} is no longer checked. Nothing in it was touched.", $"{Korean.Topic(row.Name)} 더 이상 확인하지 않습니다. 그 안의 어떤 것도 건드리지 않았습니다."), warning: false);
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

    public bool IsWindowsLanguage
    {
        get => Settings.UiLanguage.Length == 0;
        set => SetLanguage(value, string.Empty);
    }

    public bool IsEnglish
    {
        get => Settings.UiLanguage == Language.EnglishCode;
        set => SetLanguage(value, Language.EnglishCode);
    }

    public bool IsKorean
    {
        get => Settings.UiLanguage == Language.KoreanCode;
        set => SetLanguage(value, Language.KoreanCode);
    }

    /// <summary>Saved here; the app hears the change and builds its window again in the new language.</summary>
    private void SetLanguage(bool chosen, string code)
    {
        if (!chosen || Settings.UiLanguage == code) return;
        Change(s => s.UiLanguage = code);
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

        if (!Destinations.Select(d => (d.Id, d.PasswordNote)).SequenceEqual(settings.Destinations.Select(d => (d.Id, new DestinationRowViewModel(_services.Settings, d).PasswordNote))))
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

    // The particle goes on a backup's name itself. "{name} 백업은" would read "백업 백업은" for the many backup folders called 백업.
    private static string AlreadyListed(string name) => Language.Pick($"{name} is already in the list.", $"{Korean.Topic(name)} 이미 목록에 있습니다.");

    private static string InsideProtected(string folder, string protectedName) => Language.Pick(
        $"{folder} is inside {protectedName}, which Boatdrill protects. A backup on the same disk as the files it keeps would be lost with them.",
        $"{folder} 위치는 Boatdrill이 보호하는 {Korean.Folder(protectedName)} 안에 있습니다. 원본과 같은 디스크에 있는 백업은 원본과 함께 사라집니다.");

    /// <summary>A value set by hand in settings.json gets its own entry, so the drop-down never shows blank.</summary>
    private static IReadOnlyList<Choice<int>> WithCurrent(List<Choice<int>> choices, int current, Func<int, string> label)
    {
        if (choices.All(c => c.Value != current)) choices.Add(new Choice<int>(label(current), current));
        return choices.OrderBy(c => c.Value).ToList();
    }

    public void Dispose() => _services.Settings.Changed -= OnSettingsChanged;
}
