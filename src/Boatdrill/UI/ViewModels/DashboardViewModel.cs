using System.Collections.ObjectModel;
using System.Reflection;
using Boatdrill.Core.Drill;
using Boatdrill.Core.Notifications;
using Boatdrill.Core.Reporting;
using Boatdrill.Core.Settings;
using Boatdrill.Core.Text;
using Boatdrill.Platform;

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
        SaveReportCommand = new AsyncRelayCommand(SaveReportAsync, () => _services.Drills.Latest is not null);
    }

    public ObservableCollection<DestinationCardViewModel> Cards { get; } = [];

    public RelayCommand OpenSettingsCommand { get; }

    public RelayCommand RunDrillCommand { get; }

    /// <summary>Writes the latest drill as a one-page HTML report and opens it.</summary>
    public AsyncRelayCommand SaveReportCommand { get; }

    public bool HasReport => _services.Drills.Latest is not null;

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
        OnPropertyChanged(nameof(HasReport));
        SaveReportCommand.RaiseCanExecuteChanged();

        var results = Cards.Where(c => c.Result is not null).Select(c => c.Result!).ToList();
        NeedsSetup = !settings.IsReadyToDrill;
        CanRunFirstDrill = settings.IsReadyToDrill && results.Count == 0;
        Protecting = DescribeProtected(settings, latest);

        if (settings.Sources.Count == 0)
        {
            Headline = Language.Pick("Choose what to protect", "보호할 폴더를 고르세요");
            HeadlineKind = VerdictStyle.None;
            Summary = Language.Pick(
                "Pick the folders whose files you could not bear to lose. Boatdrill will check that their backup could bring them back.",
                "잃어서는 안 될 파일이 있는 폴더를 고르세요. Boatdrill이 그 백업으로 파일을 되살릴 수 있는지 확인합니다.");
            SetupButtonText = Language.Pick("Choose folders", "폴더 고르기");
        }
        else if (settings.Destinations.Count == 0)
        {
            Headline = Language.Pick("Where does your backup go?", "백업은 어디에 저장되나요?");
            HeadlineKind = VerdictStyle.None;
            Summary = Language.Pick(
                "Add the drive, network share or synced folder your backup copies to. Boatdrill only ever reads from it.",
                "백업이 복사되는 드라이브, 네트워크 공유 폴더, 동기화 폴더를 추가하세요. Boatdrill은 그곳을 읽기만 합니다.");
            SetupButtonText = Language.Pick("Add a destination", "백업 위치 추가");
        }
        else if (results.Count == 0 || latest is null)
        {
            Headline = Language.Pick("Ready for the first drill", "첫 복원 훈련을 할 준비가 되었습니다");
            HeadlineKind = VerdictStyle.None;
            Summary = Language.Pick(
                "A drill reads a sample of your files back from each backup and checks every byte against the original. It takes a few minutes.",
                "복원 훈련은 백업마다 파일 표본을 다시 읽어 원본과 한 바이트도 빠짐없이 비교합니다. 몇 분이면 끝납니다.");
        }
        else
        {
            Headline = Verdicts.Headline(results);
            HeadlineKind = results.Any(r => NotificationPolicy.IsProblem(r.Score.Verdict)) ? VerdictStyle.Bad
                : results.Any(r => r.Score.Verdict == Core.Scoring.Verdict.NothingToCheck) ? VerdictStyle.None
                : results.Any(r => r.Score.Verdict == Core.Scoring.Verdict.WouldMostlyRestore) ? VerdictStyle.Fair
                : VerdictStyle.Good;

            // A folder the drill could not find was not checked, whatever the backups say about the others.
            // One the user has since taken out of Settings is no longer their concern.
            var missing = latest.MissingSources.Where(m => settings.Sources.Any(s => s.Id == m.SourceId)).ToList();
            if (missing.Count > 0 && HeadlineKind != VerdictStyle.Bad)
            {
                Headline = Verdicts.MissingHeadline(missing);
                HeadlineKind = VerdictStyle.Bad;
            }

            var sampled = latest.Sources.Sum(s => s.Sampled);
            var reached = results.Count(r => r.Reachable);
            var ago = Humanize.Ago(now - latest.FinishedUtc);
            if (Language.IsKorean)
            {
                var from = reached == results.Count ? $"백업 {results.Count:N0}개" : $"백업 {results.Count:N0}개 중 {reached:N0}개";
                var when = Humanize.AgoWhen(now - latest.FinishedUtc);
                Summary = reached == 0
                    ? $"{when} 한 마지막 복원 훈련에서는 어떤 백업에도 연결하지 못했습니다."
                    : $"{when} 한 마지막 복원 훈련에서 표본 파일 {sampled:N0}개를 {from}에서 다시 읽어 원본과 한 바이트도 빠짐없이 비교했습니다.";
            }
            else
            {
                var from = reached == results.Count
                    ? Humanize.Plural(results.Count, "backup")
                    : $"{reached} of {Humanize.Plural(results.Count, "backup")}";
                Summary = reached == 0
                    ? $"The last drill, {ago}, could not reach any backup."
                    : $"The last drill, {ago}, read {Humanize.Plural(sampled, "sampled file")} back from {from} and compared every byte with the original.";
            }

            if (missing.Count > 0) Summary += " " + Verdicts.MissingSources(missing);
            if (Verdicts.NothingChecked(latest.Sources) is { Length: > 0 } why) Summary += " " + why;
        }
    }

    private async Task SaveReportAsync()
    {
        if (_services.Drills.Latest is not { } latest) return;

        var day = latest.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var path = Shell.PickSaveFile(
            Language.Pick("Save the drill report", "복원 훈련 보고서 저장"),
            Language.Pick($"Boatdrill report {day}.html", $"Boatdrill 보고서 {day}.html"),
            Language.Pick("Web page (*.html)|*.html", "웹 페이지 (*.html)|*.html"));
        if (path is null) return;

        var version = typeof(DashboardViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var recent = _services.Drills.Recent;
        try
        {
            await File.WriteAllTextAsync(path, HtmlReport.Render(latest, recent, version));
            Shell.OpenFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Could not save the report.", ex);
            Summary = Language.Pick("The report could not be saved: ", "보고서를 저장할 수 없습니다: ") + ex.Message;
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

            card.Apply(destination, latest?.Destinations.FirstOrDefault(r => r.DestinationId == destination.Id), latest, _services.Drills.Recent, now);
        }
    }

    private static string DescribeProtected(AppSettings settings, DrillReport? latest)
    {
        if (settings.Sources.Count == 0) return string.Empty;

        var list = Language.List(settings.Sources.Select(s => s.Name).ToList());
        var measured = latest?.Sources.Where(s => settings.Sources.Any(x => x.Id == s.SourceId)).ToList();
        if (Language.IsKorean)
        {
            return measured is { Count: > 0 }
                ? $"{Korean.Object(list)} 보호하고 있습니다: 파일 {measured.Sum(s => s.Population):N0}개, {Humanize.Bytes(measured.Sum(s => s.PopulationBytes))}."
                : $"{Korean.Object(list)} 보호하고 있습니다.";
        }

        if (measured is not { Count: > 0 }) return "Protecting " + list + ".";
        return $"Protecting {list}: {measured.Sum(s => s.Population):N0} files, {Humanize.Bytes(measured.Sum(s => s.PopulationBytes))}.";
    }
}
