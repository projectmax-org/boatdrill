using System.Collections.ObjectModel;
using Boatdrill.Core.Drill;
using Boatdrill.Core.Text;
using Boatdrill.Platform;

namespace Boatdrill.UI.ViewModels;

/// <summary>A destination's result within one past drill.</summary>
public sealed record HistoryCell(string Name, string Score, string Kind, string Verdict);

/// <summary>One past drill.</summary>
public sealed class HistoryRowViewModel
{
    public HistoryRowViewModel(DrillReport report)
    {
        When = report.StartedUtc.ToLocalTime().ToString("ddd d MMM yyyy, HH:mm");
        How = report.Trigger == DrillTrigger.Scheduled ? "Scheduled" : "Run by hand";
        Duration = Humanize.Duration(report.Duration);
        Files = Humanize.Plural(report.Sources.Sum(s => s.Sampled), "file") + " sampled";
        Destinations = report.Destinations
            .Select(d => new HistoryCell(d.DestinationName, d.Reachable ? d.Score.Total.ToString() : "–", VerdictStyle.Kind(d.Score.Verdict), VerdictStyle.Text(d.Score.Verdict)))
            .ToList();
        Kind = VerdictStyle.Kind(report.Worst);
    }

    public string When { get; }

    public string How { get; }

    public string Duration { get; }

    public string Files { get; }

    /// <summary>The worst destination's colour family, for the row's marker.</summary>
    public string Kind { get; }

    public IReadOnlyList<HistoryCell> Destinations { get; }
}

/// <summary>The last hundred drills, newest first.</summary>
public sealed class HistoryViewModel(AppServices services) : ViewModelBase
{
    private const int Shown = 100;

    public ObservableCollection<HistoryRowViewModel> Rows { get; } = [];

    public bool IsEmpty => Rows.Count == 0;

    public async Task ReloadAsync()
    {
        IReadOnlyList<DrillReport> reports;
        try
        {
            reports = await Task.Run(() => services.History.ReadRecent(Shown));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Could not read the drill history.", ex);
            return;
        }

        Rows.Clear();
        foreach (var report in reports.Reverse()) Rows.Add(new HistoryRowViewModel(report));
        OnPropertyChanged(nameof(IsEmpty));
    }
}
