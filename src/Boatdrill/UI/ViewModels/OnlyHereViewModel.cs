using System.Windows.Threading;
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

    public string ScanButtonText => _shown is null ? "Look now" : "Look again";

    public void Refresh()
    {
        var scans = _services.Scans;
        IsRunning = scans.IsRunning;
        ProgressText = scans.Progress switch
        {
            null when scans.IsRunning => "Starting…",
            null => string.Empty,
            { FilesChecked: > 0 } p => $"{p.Stage}: {p.FilesChecked:N0} files, {p.OnlyHereFiles:N0} only here so far",
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
            Headline = "What would be lost with this PC?";
            HeadlineKind = VerdictStyle.None;
            Sentence = "Boatdrill can look up every file in the protected folders in every backup and find the ones no backup holds a current copy of. " +
                       "It goes by names and dates, so it is quick, and it changes nothing.";
            WhenText = UnreachableText = string.Empty;
            Sources = [];
            Folders = Files = [];
            return;
        }

        var noCopy = report.Sources.Sum(s => s.NoCopyFiles);
        (Headline, HeadlineKind) = report switch
        {
            { Checked.Count: 0 } => ("No backup could be reached", VerdictStyle.Bad),
            { OnlyHereFiles: 0 } => ("Every file has a copy", VerdictStyle.Good),
            _ when noCopy == 0 => ($"{Humanize.Plural((int)Math.Min(report.OnlyHereFiles, int.MaxValue), "file")} changed since the last copy", VerdictStyle.Fair),
            _ => ($"{report.OnlyHereFiles:N0} {(report.OnlyHereFiles == 1 ? "file exists" : "files exist")} only on this PC", VerdictStyle.Bad),
        };
        Sentence = report.Sentence;
        WhenText = $"Looked {Humanize.Ago(now - report.FinishedUtc)}, in {Humanize.Duration(report.FinishedUtc - report.StartedUtc)}" +
                   (report.Checked.Count > 0 ? ", in " + string.Join(", ", report.Checked) + "." : ".");
        UnreachableText = report.Unreachable.Count == 0 ? string.Empty : "Not looked in: " + string.Join("; ", report.Unreachable) + ".";

        Sources = report.Sources
            .Select(s => new AtRiskSource(s.SourceName, Humanize.Bytes(s.NoCopyBytes + s.OlderBytes),
                $"{s.NoCopyFiles + s.OlderFiles:N0} of {s.Files:N0} files only here"))
            .ToList();
        Folders = report.Folders
            .Take(RowsShown)
            .Select(f => new AtRiskRow(Humanize.Bytes(f.Bytes), f.RelativePath.Length == 0 ? f.SourceName : $"{f.SourceName}\\{f.RelativePath}",
                Humanize.Plural(f.Files, "file")))
            .ToList();
        Files = report.Largest
            .Take(RowsShown)
            .Select(f => new AtRiskRow(Humanize.Bytes(f.Length), $"{f.SourceName}\\{f.RelativePath}",
                (f.HasOlderCopy ? "an older copy exists" : "no copy in any backup") + ", changed " + Humanize.Ago(now - f.LastWriteUtc)))
            .ToList();
    }

    private void OnScanChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Refresh));

    public void Dispose() => _services.Scans.StateChanged -= OnScanChanged;
}
