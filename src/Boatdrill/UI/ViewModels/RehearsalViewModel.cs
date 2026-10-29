using System.Windows.Threading;
using Boatdrill.Core.Drill;
using Boatdrill.Core.Settings;
using Boatdrill.Core.Text;
using Boatdrill.Core.Verification;
using Boatdrill.Platform;

namespace Boatdrill.UI.ViewModels;

/// <summary>The "Full restore" page: what to restore from where, the rehearsal running, and the last result.</summary>
public sealed class RehearsalViewModel : ViewModelBase, IDisposable
{
    private const int FindingsShown = 50;

    private readonly AppServices _services;
    private readonly Dispatcher _dispatcher;
    private IReadOnlyList<Choice<string>> _sources = [];
    private IReadOnlyList<Choice<string>> _destinations = [];
    private Choice<string>? _selectedSource;
    private Choice<string>? _selectedDestination;
    private string _folder = string.Empty;
    private bool _isRunning;
    private double _progressValue;
    private string _progressText = string.Empty;
    private string _headline = string.Empty;
    private string _headlineKind = VerdictStyle.None;
    private string _sentence = string.Empty;
    private string _whenText = string.Empty;
    private string _errorText = string.Empty;
    private IReadOnlyList<ScorePart> _tiles = [];
    private IReadOnlyList<FindingRow> _findings = [];
    private RehearsalResult? _shown;

    public RehearsalViewModel(AppServices services)
    {
        _services = services;
        _dispatcher = Dispatcher.CurrentDispatcher;
        StartCommand = new RelayCommand(Start,
            () => !_services.Rehearsals.IsRunning && !_services.Drills.IsRunning && !_services.Scans.IsRunning &&
                  SelectedSource is not null && SelectedDestination is not null);
        StopCommand = new RelayCommand(() => _services.Rehearsals.Cancel(), () => _services.Rehearsals.IsRunning);
        BrowseCommand = new RelayCommand(Browse, () => SelectedSource is not null);

        _services.Rehearsals.StateChanged += OnRehearsalChanged;
        _services.Settings.Changed += OnSettingsChanged;
        LoadChoices();

        // Start from what was rehearsed last, while it is still set up.
        if (_services.Rehearsals.Latest is { } last)
        {
            SelectedSource = Sources.FirstOrDefault(c => c.Value == last.SourceId) ?? SelectedSource;
            SelectedDestination = Destinations.FirstOrDefault(c => c.Value == last.DestinationId) ?? SelectedDestination;
            Folder = last.Folder;
        }

        Refresh();
    }

    public RelayCommand StartCommand { get; }

    public RelayCommand StopCommand { get; }

    public RelayCommand BrowseCommand { get; }

    public IReadOnlyList<Choice<string>> Sources
    {
        get => _sources;
        private set => Set(ref _sources, value);
    }

    public IReadOnlyList<Choice<string>> Destinations
    {
        get => _destinations;
        private set => Set(ref _destinations, value);
    }

    public Choice<string>? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (!Set(ref _selectedSource, value)) return;
            Folder = string.Empty;
            StartCommand.RaiseCanExecuteChanged();
            BrowseCommand.RaiseCanExecuteChanged();
        }
    }

    public Choice<string>? SelectedDestination
    {
        get => _selectedDestination;
        set
        {
            if (Set(ref _selectedDestination, value)) StartCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>A folder inside the chosen one, by its path below it; empty for all of it.</summary>
    public string Folder
    {
        get => _folder;
        set => Set(ref _folder, (value ?? string.Empty).Trim().Trim('\\', '/'));
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set => Set(ref _isRunning, value);
    }

    /// <summary>Share of the folder's bytes restored so far.</summary>
    public double ProgressValue
    {
        get => _progressValue;
        private set => Set(ref _progressValue, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => Set(ref _progressText, value);
    }

    public string Headline
    {
        get => _headline;
        private set => Set(ref _headline, value);
    }

    /// <summary>good, fair, bad or none: colours the headline's marker.</summary>
    public string HeadlineKind
    {
        get => _headlineKind;
        private set => Set(ref _headlineKind, value);
    }

    public string Sentence
    {
        get => _sentence;
        private set => Set(ref _sentence, value);
    }

    public string WhenText
    {
        get => _whenText;
        private set => Set(ref _whenText, value);
    }

    public string ErrorText
    {
        get => _errorText;
        private set => Set(ref _errorText, value);
    }

    public IReadOnlyList<ScorePart> Tiles
    {
        get => _tiles;
        private set => Set(ref _tiles, value);
    }

    public IReadOnlyList<FindingRow> Findings
    {
        get => _findings;
        private set => Set(ref _findings, value);
    }

    public void Refresh()
    {
        var rehearsals = _services.Rehearsals;
        IsRunning = rehearsals.IsRunning;
        var progress = rehearsals.Progress;
        ProgressValue = progress is { BytesTotal: > 0 } ? (double)progress.BytesDone / progress.BytesTotal : 0;
        ProgressText = progress switch
        {
            null when rehearsals.IsRunning => Language.Pick("Starting…", "시작하는 중…"),
            null => string.Empty,
            { FilesTotal: > 0 } p => Language.Pick(
                $"{p.Stage}: {p.FilesDone:N0} of {p.FilesTotal:N0} files, {Humanize.Bytes(p.BytesDone)} of {Humanize.Bytes(p.BytesTotal)}",
                $"{p.Stage}: 파일 {p.FilesTotal:N0}개 중 {p.FilesDone:N0}개, {Humanize.Bytes(p.BytesTotal)} 중 {Humanize.Bytes(p.BytesDone)}"),
            var p => p.Stage + "…",
        };
        ErrorText = rehearsals.IsRunning ? string.Empty : rehearsals.LastError ?? string.Empty;
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();

        var result = rehearsals.Latest;
        if (result is not null && ReferenceEquals(result, _shown)) return;
        _shown = result;
        Apply(result, DateTimeOffset.UtcNow);
    }

    private void Apply(RehearsalResult? r, DateTimeOffset now)
    {
        if (r is null)
        {
            Headline = Language.Pick("Rehearse a full restore", "전체 복원 리허설");
            HeadlineKind = VerdictStyle.None;
            Sentence = Language.Pick(
                "A drill reads a sample. A rehearsal restores every file of one folder from one backup and checks each against the original, " +
                "so you know it would all come back, and how long it would take.",
                "복원 훈련은 표본만 읽습니다. 리허설은 한 폴더의 모든 파일을 한 백업에서 복원해 하나하나 원본과 비교하므로, " +
                "전부 돌아올지, 그리고 시간이 얼마나 걸릴지 알 수 있습니다.");
            WhenText = string.Empty;
            Tiles = [];
            Findings = [];
            return;
        }

        var what = r.Folder.Length == 0 ? r.SourceName : r.SourceName + "\\" + r.Folder;
        var notIntact = r.Files - r.Verified;
        (Headline, HeadlineKind) = (r, Language.IsKorean) switch
        {
            ({ Reachable: false }, false) => ($"{r.DestinationName} could not be reached", VerdictStyle.Bad),
            ({ Reachable: false }, true) => ($"{r.DestinationName}에 연결할 수 없습니다", VerdictStyle.Bad),
            ({ Refusal: not null }, _) => (Language.Pick("The rehearsal did not start", "리허설을 시작하지 못했습니다"), VerdictStyle.Fair),
            ({ Files: 0 }, false) => ($"{what} has no files to restore", VerdictStyle.None),
            ({ Files: 0 }, true) => ($"{what}에는 복원할 파일이 없습니다", VerdictStyle.None),
            ({ AllIntact: true }, false) => ($"All of {what} came back intact", VerdictStyle.Good),
            ({ AllIntact: true }, true) => ($"{what} 전체가 그대로 돌아왔습니다", VerdictStyle.Good),
            (_, false) when r.Missing + r.Corrupt + r.Errors > 0 => ($"{notIntact:N0} of {r.Files:N0} files in {what} did not come back intact", VerdictStyle.Bad),
            (_, true) when r.Missing + r.Corrupt + r.Errors > 0 => ($"{what}의 파일 {r.Files:N0}개 중 {notIntact:N0}개가 그대로 돌아오지 않았습니다", VerdictStyle.Bad),
            (_, false) => ($"{notIntact:N0} of {r.Files:N0} files in {what} came back older", VerdictStyle.Fair),
            (_, true) => ($"{what}의 파일 {r.Files:N0}개 중 {notIntact:N0}개가 이전 버전으로 돌아왔습니다", VerdictStyle.Fair),
        };

        // Written again from the result's numbers, so it reads in the language Boatdrill speaks now.
        Sentence = RehearsalRunner.Sentence(r);
        var ago = Humanize.Ago(now - r.FinishedUtc);
        var took = Humanize.Duration(r.FinishedUtc - r.StartedUtc);
        WhenText = Language.Pick(
            $"Rehearsed {ago} from {r.DestinationName}, in {took} altogether.",
            $"{ago}에 {r.DestinationName}에서 리허설했고, 모두 {Korean.Subject(took)} 걸렸습니다.");

        var older = r.Outdated + r.Placeholder;
        var broken = r.Corrupt + r.Errors;
        Tiles = r.Reachable && r.Refusal is null && r.Files > 0
            ?
            [
                new ScorePart(Language.Pick("Came back intact", "그대로 돌아온 파일"), $"{r.Verified:N0}",
                    Language.Pick($"of {r.Files:N0} files, {Humanize.Bytes(r.Bytes)}", $"전체 {r.Files:N0}개, {Humanize.Bytes(r.Bytes)}")),
                new ScorePart(Language.Pick("No copy", "사본 없음"), $"{r.Missing:N0}",
                    r.Missing == 0 ? Language.Pick("every file has one", "모든 파일에 사본이 있음") : Language.Pick("no copy in the backup", "백업에 사본이 없음")),
                new ScorePart(Language.Pick("Older, or cloud only", "이전 버전, 클라우드 전용"), $"{older:N0}",
                    older == 0 ? Language.Pick("all up to date", "모두 최신 상태") : Language.Pick("not the latest bytes here", "최신 내용이 여기에 없음")),
                new ScorePart(Language.Pick("Corrupt or unreadable", "손상, 읽을 수 없음"), $"{broken:N0}",
                    broken == 0 ? Language.Pick("none", "없음") : Language.Pick("would not restore as they are", "이대로는 복원되지 않음")),
            ]
            : [];
        Findings = r.Findings
            .Take(FindingsShown)
            .Select(f => new FindingRow(VerdictStyle.Text(f.Outcome), VerdictStyle.Kind(f.Outcome), f.RelativePath, f.Detail ?? string.Empty))
            .ToList();
    }

    private void Start()
    {
        if (SelectedSource is { } source && SelectedDestination is { } destination)
        {
            _ = _services.Rehearsals.RunAsync(source.Value, destination.Value, Folder);
        }
    }

    /// <summary>Picks a folder inside the chosen one; anything outside it is refused.</summary>
    private void Browse()
    {
        if (_services.Settings.Settings.Sources.FirstOrDefault(s => s.Id == SelectedSource?.Value) is not { } source) return;
        if (Shell.PickFolder(Language.Pick($"Choose a folder inside {source.Name}", $"{source.Name} 안의 폴더 고르기")) is not { } picked) return;

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.Folder));
        var chosen = Path.TrimEndingDirectorySeparator(Path.GetFullPath(picked));
        var relative = Path.GetRelativePath(root, chosen);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            ErrorText = Language.Pick($"Choose a folder inside {source.Folder}.", $"{source.Folder} 안에 있는 폴더를 고르세요.");
            return;
        }

        ErrorText = string.Empty;
        Folder = relative == "." ? string.Empty : relative;
    }

    /// <summary>The protected folders and the backups, keeping what was chosen when it still exists.</summary>
    private void LoadChoices()
    {
        var settings = _services.Settings.Settings;
        Sources = settings.Sources.Select(s => new Choice<string>(s.Name, s.Id)).ToList();
        Destinations = settings.Destinations.Select(d => new Choice<string>(d.Name, d.Id)).ToList();
        SelectedSource = Sources.FirstOrDefault(c => c.Value == SelectedSource?.Value) ?? Sources.FirstOrDefault();
        SelectedDestination = Destinations.FirstOrDefault(c => c.Value == SelectedDestination?.Value) ?? Destinations.FirstOrDefault();
    }

    private void OnRehearsalChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Refresh));

    private void OnSettingsChanged(object? sender, AppSettings settings) => _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(LoadChoices));

    public void Dispose()
    {
        _services.Rehearsals.StateChanged -= OnRehearsalChanged;
        _services.Settings.Changed -= OnSettingsChanged;
    }
}
