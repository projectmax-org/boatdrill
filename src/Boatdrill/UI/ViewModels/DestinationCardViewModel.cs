using System.Windows;
using System.Windows.Media;
using Boatdrill.Core;
using Boatdrill.Core.Drill;
using Boatdrill.Core.Settings;
using Boatdrill.Core.Text;
using Boatdrill.Platform;

namespace Boatdrill.UI.ViewModels;

// The two records below are immutable and bound OneTime: WPF keeps alive any object that it binds to
// without INotifyPropertyChanged, so a one-way binding to them would leak on every refresh.

/// <summary>One of the four parts of the score, as a small tile.</summary>
public sealed record ScorePart(string Name, string Value, string Detail);

/// <summary>A sampled file that did not verify.</summary>
public sealed record FindingRow(string Outcome, string Kind, string Path, string Detail);

/// <summary>A destination on the dashboard and the latest drill's word on it.</summary>
public sealed class DestinationCardViewModel : ViewModelBase
{
    private const int FindingsShown = 50;

    /// <summary>The trend line's box, in device-independent pixels.</summary>
    public const double TrendWidth = 96;
    public const double TrendHeight = 26;

    private string _name = string.Empty;
    private string _folder = string.Empty;
    private string _scoreText = "–";
    private string _verdictText = string.Empty;
    private string _verdictKind = VerdictStyle.None;
    private string _sentence = string.Empty;
    private string _checkedText = string.Empty;
    private IReadOnlyList<ScorePart> _parts = [];
    private IReadOnlyList<FindingRow> _findings = [];
    private string _findingsNote = string.Empty;
    private bool _showFindings;
    private DestinationKind _kind;
    private bool _canOpenFolder = true;
    private PointCollection _trendPoints = [];
    private Geometry _trendMisses = Geometry.Empty;
    private bool _hasTrend;
    private string _trendText = string.Empty;
    private string? _trendKey;

    public DestinationCardViewModel(string id)
    {
        Id = id;
        ToggleFindingsCommand = new RelayCommand(() => ShowFindings = !ShowFindings);
        OpenFolderCommand = new RelayCommand(() => Shell.OpenFolder(_location));
    }

    public string Id { get; }

    public DestinationResult? Result { get; private set; }

    public RelayCommand ToggleFindingsCommand { get; }

    public RelayCommand OpenFolderCommand { get; }

    public string Name
    {
        get => _name;
        private set => Set(ref _name, value);
    }

    /// <summary>Where the backup is, as shown: the folder, the File History drive or the repository.</summary>
    public string Folder
    {
        get => _folder;
        private set => Set(ref _folder, value);
    }

    public DestinationKind Kind
    {
        get => _kind;
        private set => Set(ref _kind, value);
    }

    /// <summary>A repository behind a URL has no folder to open.</summary>
    public bool CanOpenFolder
    {
        get => _canOpenFolder;
        private set => Set(ref _canOpenFolder, value);
    }

    /// <summary>The score in recent drills, as a line in a <see cref="TrendWidth"/> by <see cref="TrendHeight"/> box.</summary>
    public PointCollection TrendPoints
    {
        get => _trendPoints;
        private set => Set(ref _trendPoints, value);
    }

    /// <summary>A dot on the baseline for each recent drill that could not reach the backup.</summary>
    public Geometry TrendMisses
    {
        get => _trendMisses;
        private set => Set(ref _trendMisses, value);
    }

    public bool HasTrend
    {
        get => _hasTrend;
        private set => Set(ref _hasTrend, value);
    }

    /// <summary>The trend in words, for the tooltip and screen readers.</summary>
    public string TrendText
    {
        get => _trendText;
        private set => Set(ref _trendText, value);
    }

    public string ScoreText
    {
        get => _scoreText;
        private set => Set(ref _scoreText, value);
    }

    public string VerdictText
    {
        get => _verdictText;
        private set => Set(ref _verdictText, value);
    }

    /// <summary>good, fair, bad or none.</summary>
    public string VerdictKind
    {
        get => _verdictKind;
        private set => Set(ref _verdictKind, value);
    }

    public string Sentence
    {
        get => _sentence;
        private set => Set(ref _sentence, value);
    }

    public string CheckedText
    {
        get => _checkedText;
        private set => Set(ref _checkedText, value);
    }

    public IReadOnlyList<ScorePart> Parts
    {
        get => _parts;
        private set
        {
            if (_parts.SequenceEqual(value)) return;
            _parts = value;
            Raise(nameof(Parts), nameof(HasParts));
        }
    }

    public bool HasParts => Parts.Count > 0;

    public IReadOnlyList<FindingRow> Findings
    {
        get => _findings;
        private set
        {
            if (_findings.SequenceEqual(value)) return;
            _findings = value;
            Raise(nameof(Findings), nameof(HasFindings), nameof(FindingsToggleText));
            if (value.Count == 0) ShowFindings = false;
        }
    }

    public bool HasFindings => Findings.Count > 0;

    /// <summary>Says when only the first files are listed.</summary>
    public string FindingsNote
    {
        get => _findingsNote;
        private set => Set(ref _findingsNote, value);
    }

    public bool ShowFindings
    {
        get => _showFindings;
        set
        {
            if (Set(ref _showFindings, value)) OnPropertyChanged(nameof(FindingsToggleText));
        }
    }

    public string FindingsToggleText => ShowFindings
        ? "Hide the files"
        : "Show " + (Result is { } r && r.Findings.Count == 1 ? "the file" : "the files") + " that need a look";

    private string _location = string.Empty;

    public void Apply(DestinationSetting destination, DestinationResult? result, DrillReport? report, IReadOnlyList<DrillReport> recent, DateTimeOffset now)
    {
        Name = destination.Name;
        Kind = destination.Kind;
        _location = destination.Folder;
        Folder = destination.Kind switch
        {
            DestinationKind.FileHistory => "File History on " + destination.Folder,
            DestinationKind.Restic => "restic repository at " + Destination.Redact(destination.Folder),
            _ => destination.Folder,
        };
        CanOpenFolder = !(destination.Kind == DestinationKind.Restic && Destination.IsRepositoryUrl(destination.Folder));
        Result = result;
        ApplyTrend(recent);

        if (result is null || report is null)
        {
            ScoreText = "–";
            VerdictText = "not checked yet";
            VerdictKind = VerdictStyle.None;
            Sentence = "The next drill will say whether this backup would restore.";
            CheckedText = string.Empty;
            Parts = [];
            Findings = [];
            FindingsNote = string.Empty;
            return;
        }

        var score = result.Score;
        ScoreText = result.Reachable ? score.Total.ToString() : "–";
        VerdictText = VerdictStyle.Text(score.Verdict);
        VerdictKind = VerdictStyle.Kind(score.Verdict);
        Sentence = result.Sentence;
        CheckedText = "Checked " + Humanize.Ago(now - report.FinishedUtc);

        Parts = result.Reachable && result.Sampled > 0
            ?
            [
                new ScorePart("Coverage", Percent(score.Coverage),
                    $"{result.Sampled - result.Missing - result.Placeholder - result.Errors:N0} of {result.Sampled:N0} found" +
                    (score.CoverageMargin >= 0.005 ? $", ±{Percent(score.CoverageMargin)}" : string.Empty)),
                new ScorePart("Freshness", Percent(score.Freshness),
                    result.Lag is { } lag && lag > TimeSpan.Zero ? Humanize.Age(lag) + " behind"
                    : result.CanaryCopiedUtc is not null ? "up to date, by the canary"
                    : "up to date"),
                new ScorePart("Integrity", Percent(score.Integrity), IntegrityDetail(result)),
                new ScorePart("Reachability", Percent(score.Reachability),
                    score.Reachability >= 0.995 ? "there at every drill" : "missing at some drills"),
            ]
            : [];

        Findings = result.Findings
            .Take(FindingsShown)
            .Select(f => new FindingRow(VerdictStyle.Text(f.Outcome), VerdictStyle.Kind(f.Outcome), f.RelativePath,
                (f.Recheck ? "Read again: " : string.Empty) + (f.Detail ?? string.Empty)))
            .ToList();

        var notVerified = result.Sampled - result.Verified + result.Findings.Count(f => f.Recheck);
        FindingsNote = result.Findings.Count > FindingsShown || notVerified > result.Findings.Count
            ? $"The first {Findings.Count} of {notVerified:N0} files that did not verify."
            : string.Empty;

        OnPropertyChanged(nameof(FindingsToggleText));
    }

    private static string IntegrityDetail(DestinationResult r) => (r.Corrupt, r.Rotted) switch
    {
        (> 0, > 0) => $"{r.Corrupt:N0} corrupt, {r.Rotted:N0} changed since verified",
        (1, _) => "1 corrupt copy",
        (> 1, _) => $"{r.Corrupt:N0} corrupt copies",
        (_, 1) => "1 copy changed since verified",
        (_, > 1) => $"{r.Rotted:N0} copies changed since verified",
        _ when r.Rechecked > 0 => $"{r.Rechecked:N0} read again, unchanged",
        _ => "no corrupt copies",
    };

    /// <summary>
    /// The score at each recent drill that checked this destination, and a dot for each that could not reach it.
    /// Rebuilt only when those scores change: a running drill refreshes the cards several times a second.
    /// </summary>
    private void ApplyTrend(IReadOnlyList<DrillReport> recent)
    {
        var history = recent.Select(r => r.Destinations.FirstOrDefault(d => d.DestinationId == Id)).OfType<DestinationResult>().ToList();
        var key = string.Join(",", history.Select(h => h.Reachable ? h.Score.Total : -1));
        if (key == _trendKey) return;
        _trendKey = key;

        var points = new PointCollection();
        var misses = new GeometryGroup();
        for (var i = 0; i < history.Count && history.Count >= 2; i++)
        {
            var x = 2 + i * (TrendWidth - 4) / (history.Count - 1);
            if (history[i].Reachable) points.Add(new Point(x, 1 + (TrendHeight - 2) * (1 - history[i].Score.Total / 100.0)));
            else misses.Children.Add(new EllipseGeometry(new Point(x, TrendHeight - 2), 2, 2));
        }

        points.Freeze();
        misses.Freeze();
        TrendPoints = points;
        TrendMisses = misses;
        HasTrend = history.Count >= 2;
        TrendText = history.Count < 2
            ? string.Empty
            : $"Score in the last {history.Count} drills: " + string.Join(", ", history.Select(h => h.Reachable ? h.Score.Total.ToString() : "not reached"));
    }

    /// <summary>Whole percents, except that a share just short of all (or just above none) keeps a decimal, so 99.6% never reads as 100%.</summary>
    private static string Percent(double share)
    {
        var whole = Math.Round(share * 100);
        return (whole == 100 && share < 1) || (whole == 0 && share > 0)
            ? $"{Math.Round(share * 100, 1, MidpointRounding.ToZero):0.0}%"
            : $"{whole:0}%";
    }
}
