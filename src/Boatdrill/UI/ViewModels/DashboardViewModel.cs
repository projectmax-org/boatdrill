using System.Collections.ObjectModel;
using Boatdrill.Core.Drill;
using Boatdrill.Core.Notifications;
using Boatdrill.Core.Settings;
using Boatdrill.Core.Text;

namespace Boatdrill.UI.ViewModels;

/// <summary>One card per destination, in the order Settings lists them, showing the latest drill's result.</summary>
public sealed class DashboardViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private string _headline = string.Empty;
    private string _headlineKind = VerdictStyle.None;
    private string _summary = string.Empty;
    private string _protecting = string.Empty;
    private bool _needsSetup;
    private string _setupButtonText = string.Empty;
    private bool _canRunFirstDrill;

    public DashboardViewModel(AppServices services, MainViewModel main)
    {
        _services = services;
        OpenSettingsCommand = main.ShowSettingsCommand;
        RunDrillCommand = main.RunDrillCommand;
    }

    public ObservableCollection<DestinationCardViewModel> Cards { get; } = [];

    public RelayCommand OpenSettingsCommand { get; }

    public RelayCommand RunDrillCommand { get; }

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

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    public string Protecting
    {
        get => _protecting;
        private set => Set(ref _protecting, value);
    }

    /// <summary>Nothing to protect or nowhere to look yet: the card that sends the user to Settings shows.</summary>
    public bool NeedsSetup
    {
        get => _needsSetup;
        private set => Set(ref _needsSetup, value);
    }

    public string SetupButtonText
    {
        get => _setupButtonText;
        private set => Set(ref _setupButtonText, value);
    }

    /// <summary>Set up, with no drill yet: the card that invites the first drill shows.</summary>
    public bool CanRunFirstDrill
    {
        get => _canRunFirstDrill;
        private set => Set(ref _canRunFirstDrill, value);
    }

    public void Refresh()
    {
        var settings = _services.Settings.Settings;
        var latest = _services.Drills.Latest;
        var now = DateTimeOffset.UtcNow;

        SyncCards(settings, latest, now);

        var results = Cards.Where(c => c.Result is not null).Select(c => c.Result!).ToList();
        NeedsSetup = !settings.IsReadyToDrill;
        CanRunFirstDrill = settings.IsReadyToDrill && results.Count == 0;
        Protecting = DescribeProtected(settings, latest);

        if (settings.Sources.Count == 0)
        {
            Headline = "Choose what to protect";
            HeadlineKind = VerdictStyle.None;
            Summary = "Pick the folders whose files you could not bear to lose. Boatdrill will check that their backup could bring them back.";
            SetupButtonText = "Choose folders";
        }
        else if (settings.Destinations.Count == 0)
        {
            Headline = "Where does your backup go?";
            HeadlineKind = VerdictStyle.None;
            Summary = "Add the drive, network share or synced folder your backup copies to. Boatdrill only ever reads from it.";
            SetupButtonText = "Add a destination";
        }
        else if (results.Count == 0 || latest is null)
        {
            Headline = "Ready for the first drill";
            HeadlineKind = VerdictStyle.None;
            Summary = "A drill reads a sample of your files back from each backup and checks every byte against the original. It takes a few minutes.";
        }
        else
        {
            var problems = results.Where(r => NotificationPolicy.IsProblem(r.Score.Verdict)).ToList();
            var fair = results.Count(r => VerdictStyle.Kind(r.Score.Verdict) == VerdictStyle.Fair);
            (Headline, HeadlineKind) = problems.Count switch
            {
                0 when fair == 0 => (results.Count == 1 ? "Your backup would restore" : "Your backups would restore", VerdictStyle.Good),
                0 => ("Your backups would mostly restore", VerdictStyle.Fair),
                1 => ($"{problems[0].DestinationName} {VerdictStyle.Text(problems[0].Score.Verdict)}", VerdictStyle.Bad),
                _ => ($"{problems.Count} backups need attention", VerdictStyle.Bad),
            };

            var sampled = latest.Sources.Sum(s => s.Sampled);
            var reached = results.Count(r => r.Reachable);
            var from = reached == results.Count
                ? Humanize.Plural(results.Count, "backup")
                : $"{reached} of {Humanize.Plural(results.Count, "backup")}";
            Summary = reached == 0
                ? $"The last drill, {Humanize.Ago(now - latest.FinishedUtc)}, could not reach any backup."
                : $"The last drill, {Humanize.Ago(now - latest.FinishedUtc)}, read {Humanize.Plural(sampled, "sampled file")} back from " +
                  $"{from} and compared every byte with the original.";
        }
    }

    private void SyncCards(AppSettings settings, DrillReport? latest, DateTimeOffset now)
    {
        var wanted = settings.Destinations;
        for (var i = Cards.Count - 1; i >= 0; i--)
        {
            if (wanted.All(d => d.Id != Cards[i].Id)) Cards.RemoveAt(i);
        }

        for (var i = 0; i < wanted.Count; i++)
        {
            var destination = wanted[i];
            var card = Cards.FirstOrDefault(c => c.Id == destination.Id);
            if (card is null)
            {
                card = new DestinationCardViewModel(destination.Id);
                Cards.Insert(Math.Min(i, Cards.Count), card);
            }
            else
            {
                var at = Cards.IndexOf(card);
                if (at != i) Cards.Move(at, i);
            }

            card.Apply(destination, latest?.Destinations.FirstOrDefault(r => r.DestinationId == destination.Id), latest, now);
        }
    }

    private static string DescribeProtected(AppSettings settings, DrillReport? latest)
    {
        if (settings.Sources.Count == 0) return string.Empty;

        var names = settings.Sources.Select(s => s.Name).ToList();
        var list = names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
        var measured = latest?.Sources.Where(s => settings.Sources.Any(x => x.Id == s.SourceId)).ToList();
        if (measured is not { Count: > 0 }) return "Protecting " + list + ".";

        return $"Protecting {list}: {measured.Sum(s => s.Population):N0} files, {Humanize.Bytes(measured.Sum(s => s.PopulationBytes))}.";
    }
}
