using System.Windows.Threading;
using Boatdrill.Core.Drill;
using Boatdrill.Core.Scan;
using Boatdrill.Core.Text;

namespace Boatdrill.UI.ViewModels;

// Immutable and bound OneTime, like the dashboard's rows: see DestinationCardViewModel.

/// <summary>A folder or a file at risk, in a list.</summary>
public sealed record AtRiskRow(string Size, string Path, string Detail);

/// <summary>One protected folder's share of the scan, as a tile.</summary>
public sealed record AtRiskSource(string Name, string Value, string Detail);

/// <summary>The "only on this PC" page: the last scan, and the button that starts the next.</summary>
public sealed class OnlyHereViewModel : ViewModelBase, IDisposable
{
    private const int RowsShown = 15;

    private readonly AppServices _services;
    private readonly Dispatcher _dispatcher;
    private bool _isRunning;
    private string _progressText = string.Empty;
    private string _headline = string.Empty;
    private string _headlineKind = VerdictStyle.None;
    private string _sentence = string.Empty;
    private string _whenText = string.Empty;
    private string _unreachableText = string.Empty;
    private string _errorText = string.Empty;
    private IReadOnlyList<AtRiskSource> _sources = [];
    private IReadOnlyList<AtRiskRow> _folders = [];
    private IReadOnlyList<AtRiskRow> _files = [];
    private OnlyHereReport? _shown;

    public OnlyHereViewModel(AppServices services)
    {
        _services = services;
        _dispatcher = Dispatcher.CurrentDispatcher;
        ScanCommand = new RelayCommand(() => _ = _services.Scans.RunAsync(),
            () => !_services.Scans.IsRunning && !_services.Drills.IsRunning && _services.Settings.Settings.IsReadyToDrill);
        StopCommand = new RelayCommand(() => _services.Scans.Cancel(), () => _services.Scans.IsRunning);
        _services.Scans.StateChanged += OnScanChanged;
        Refresh();
    }

    public RelayCommand ScanCommand { get; }

    public RelayCommand StopCommand { get; }

    public bool IsRunning
    {
        get => _isRunning;
        private set => Set(ref _isRunning, value);
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

    public string UnreachableText
    {
        get => _unreachableText;
        private set => Set(ref _unreachableText, value);
    }

    /// <summary>Why the last scan did not run or finish.</summary>
    public string ErrorText
    {
        get => _errorText;
        private set => Set(ref _errorText, value);
    }

    public IReadOnlyList<AtRiskSource> Sources
    {
        get => _sources;
        private set => Set(ref _sources, value);
    }

    public IReadOnlyList<AtRiskRow> Folders
    {
        get => _folders;
        private set => Set(ref _folders, value);
    }

    public IReadOnlyList<AtRiskRow> Files
    {
        get => _files;
        private set => Set(ref _files, value);
    }

    public string ScanButtonText => _shown is null ? Language.Pick("Look now", "지금 살펴보기") : Language.Pick("Look again", "다시 살펴보기");

    public void Refresh()
    {
        var scans = _services.Scans;
        IsRunning = scans.IsRunning;
        ProgressText = scans.Progress switch
        {
            null when scans.IsRunning => Language.Pick("Starting…", "시작하는 중…"),
            null => string.Empty,
            { FilesChecked: > 0 } p => Language.Pick(
                $"{p.Stage}: {p.FilesChecked:N0} files, {p.OnlyHereFiles:N0} only here so far",
                $"{p.Stage}: 파일 {p.FilesChecked:N0}개 확인, 지금까지 이 PC에만 있는 파일 {p.OnlyHereFiles:N0}개"),
            var p => p.Stage + "…",
        };
        ErrorText = scans.IsRunning ? string.Empty : scans.LastError ?? string.Empty;
        ScanCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();

        var report = scans.Latest;
        if (report is not null && ReferenceEquals(report, _shown)) return;
        _shown = report;
        OnPropertyChanged(nameof(ScanButtonText));
        Apply(report, DateTimeOffset.UtcNow);
    }

    private void Apply(OnlyHereReport? report, DateTimeOffset now)
    {
        if (report is null)
        {
            Headline = Language.Pick("What would be lost with this PC?", "이 PC를 잃으면 무엇이 사라질까요?");
            HeadlineKind = VerdictStyle.None;
            Sentence = Language.Pick(
                "Boatdrill can look up every file in the protected folders in every backup and find the ones no backup holds a current copy of. " +
                "It goes by names and dates, so it is quick, and it changes nothing.",
                "Boatdrill은 보호하는 폴더의 모든 파일을 모든 백업에서 찾아보고, 어느 백업에도 최신 사본이 없는 파일을 가려냅니다. " +
                "이름과 날짜만 비교하므로 빠르고, 아무것도 바꾸지 않습니다.");
            WhenText = UnreachableText = string.Empty;
            Sources = [];
            Folders = Files = [];
            return;
        }

        var noCopy = report.Sources.Sum(s => s.NoCopyFiles);
        var korean = Language.IsKorean;
        (Headline, HeadlineKind) = report switch
        {
            { Checked.Count: 0 } => (Language.Pick("No backup could be looked in", "살펴볼 수 있는 백업이 없습니다"), VerdictStyle.Bad),
            { OnlyHereFiles: 0, NotCompared.Count: > 0 } => (Language.Pick("Not every folder could be compared", "비교하지 못한 폴더가 있습니다"), VerdictStyle.Fair),
            // No file looked up is not every file having a copy: the folders are empty, or all their files are online only.
            { OnlyHereFiles: 0 } when report.Sources.Sum(s => s.Files) == 0 => (Language.Pick("There was no file to look up", "살펴볼 파일이 없었습니다"), VerdictStyle.None),
            // With online-only files left out, "every file" is said of the files on this PC: nobody looked for a copy of the others.
            { OnlyHereFiles: 0, OnlineOnlyFiles: > 0 } => (Language.Pick("Every file on this PC has a copy", "이 PC에 있는 파일은 모두 사본이 있습니다"), VerdictStyle.Good),
            { OnlyHereFiles: 0 } => (Language.Pick("Every file has a copy", "모든 파일에 사본이 있습니다"), VerdictStyle.Good),
            _ when noCopy == 0 => (korean
                ? $"마지막으로 복사된 뒤에 바뀐 파일이 {report.OnlyHereFiles:N0}개 있습니다"
                : $"{Humanize.Plural(report.OnlyHereFiles, "file")} changed since the last copy", VerdictStyle.Fair),
            _ => (korean
                ? $"이 PC에만 있는 파일이 {report.OnlyHereFiles:N0}개 있습니다"
                : $"{report.OnlyHereFiles:N0} {(report.OnlyHereFiles == 1 ? "file exists" : "files exist")} only on this PC", VerdictStyle.Bad),
        };

        // A folder that was not there was not looked through, so its files may have no copy at all: as on the
        // dashboard, that outranks everything short of bad news, "every file has a copy" first of all.
        if (report.NotFound.Count > 0 && HeadlineKind != VerdictStyle.Bad)
        {
            Headline = Verdicts.MissingHeadline(report.NotFound.Select(s => s.SourceName).ToList());
            HeadlineKind = VerdictStyle.Bad;
        }

        // Written again from the report's numbers, so it reads in the language Boatdrill speaks now.
        Sentence = OnlyHereScanner.Sentence(report);
        var ago = Humanize.Ago(now - report.FinishedUtc);
        var took = Humanize.Duration(report.FinishedUtc - report.StartedUtc);
        WhenText = korean
            ? $"{Humanize.AgoWhen(now - report.FinishedUtc)} 살펴봤고, {Korean.Subject(took)} 걸렸습니다." + (report.Checked.Count > 0 ? " 살펴본 백업: " + string.Join(", ", report.Checked) + "." : string.Empty)
            : $"Looked {ago}, in {took}" + (report.Checked.Count > 0 ? ", in " + string.Join(", ", report.Checked) + "." : ".");
        UnreachableText = report.Unreachable.Count == 0
            ? string.Empty
            : Language.Pick("Not looked in: ", "살펴보지 못한 백업: ") + string.Join("; ", report.Unreachable) + ".";

        Sources = report.Sources
            .Select(s => new AtRiskSource(s.SourceName, Humanize.Bytes(s.NoCopyBytes + s.OlderBytes),
                Language.Pick($"{s.NoCopyFiles + s.OlderFiles:N0} of {s.Files:N0} files only here", $"파일 {s.Files:N0}개 중 {s.NoCopyFiles + s.OlderFiles:N0}개가 이 PC에만 있음")))
            .ToList();
        Folders = report.Folders
            .Take(RowsShown)
            .Select(f => new AtRiskRow(Humanize.Bytes(f.Bytes), f.RelativePath.Length == 0 ? f.SourceName : $"{f.SourceName}\\{f.RelativePath}",
                Humanize.Count(f.Files, "file", "파일")))
            .ToList();
        Files = report.Largest
            .Take(RowsShown)
            .Select(f => new AtRiskRow(Humanize.Bytes(f.Length), $"{f.SourceName}\\{f.RelativePath}",
                korean
                    ? (f.HasOlderCopy ? "예전 사본만 있음" : "어느 백업에도 사본 없음") + ", " + Humanize.Ago(now - f.LastWriteUtc) + " 바뀜"
                    : (f.HasOlderCopy ? "an older copy exists" : "no copy in any backup") + ", changed " + Humanize.Ago(now - f.LastWriteUtc)))
            .ToList();
    }

    private void OnScanChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Refresh));

    public void Dispose() => _services.Scans.StateChanged -= OnScanChanged;
}
