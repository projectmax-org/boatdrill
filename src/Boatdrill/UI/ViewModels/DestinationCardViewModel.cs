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

    public DestinationCardViewModel(string id)
    {
        Id = id;
        ToggleFindingsCommand = new RelayCommand(() => ShowFindings = !ShowFindings);
        OpenFolderCommand = new RelayCommand(() => Shell.OpenFolder(Folder));
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

    public string Folder
    {
        get => _folder;
        private set => Set(ref _folder, value);
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

    public void Apply(DestinationSetting destination, DestinationResult? result, DrillReport? report, DateTimeOffset now)
    {
        Name = destination.Name;
        Folder = destination.Folder;
        Result = result;

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
                    result.Lag is { } lag && lag > TimeSpan.Zero ? Humanize.Age(lag) + " behind" : "up to date"),
                new ScorePart("Integrity", Percent(score.Integrity),
                    result.Corrupt switch { 0 => "no corrupt copies", 1 => "1 corrupt copy", var n => $"{n:N0} corrupt copies" }),
                new ScorePart("Reachability", Percent(score.Reachability),
                    score.Reachability >= 0.995 ? "there at every drill" : "missing at some drills"),
            ]
            : [];

        Findings = result.Findings
            .Take(FindingsShown)
            .Select(f => new FindingRow(VerdictStyle.Text(f.Outcome), VerdictStyle.Kind(f.Outcome), f.RelativePath, f.Detail ?? string.Empty))
            .ToList();

        var notVerified = result.Sampled - result.Verified;
        FindingsNote = result.Findings.Count > FindingsShown || notVerified > result.Findings.Count
            ? $"The first {Findings.Count} of {notVerified:N0} files that did not verify."
            : string.Empty;

        OnPropertyChanged(nameof(FindingsToggleText));
    }

    private static string Percent(double share) => $"{Math.Round(share * 100):0}%";
}
