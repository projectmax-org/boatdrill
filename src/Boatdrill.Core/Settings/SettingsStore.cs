using System.Text.Json;

namespace Boatdrill.Core.Settings;

/// <summary>
/// Loads and saves <see cref="AppSettings"/>. Every change is written at once, through a temporary
/// file, so a power cut mid-write cannot leave an unreadable file behind. An unreadable file is kept
/// beside the new one as settings.json.broken rather than overwritten.
/// </summary>
public sealed class SettingsStore
{
    private readonly Lock _gate = new();
    private readonly Func<AppSettings> _defaults;
    private readonly Func<string, string?>? _protect;

    /// <param name="protect">
    /// Encrypts a secret for the file, or returns null when it cannot; the app passes DPAPI here. With it, a password
    /// written into a destination's URL stays out of the file from the next save on, in settings an earlier version
    /// wrote too: the URL is saved with *** in its place, and in full only encrypted. Without it, or while it fails,
    /// the URL is saved as it is.
    /// </param>
    public SettingsStore(string filePath, Func<AppSettings> defaults, Func<string, string?>? protect = null)
    {
        FilePath = filePath;
        _defaults = defaults;
        _protect = protect;
        Settings = Load();
    }

    public string FilePath { get; }

    /// <summary>The live settings. Change them through <see cref="Update"/> so they are saved and announced.</summary>
    public AppSettings Settings { get; private set; }

    /// <summary>Raised after every <see cref="Update"/>, on the thread that made it.</summary>
    public event EventHandler<AppSettings>? Changed;

    public void Update(Action<AppSettings> mutate)
    {
        lock (_gate)
        {
            mutate(Settings);
            Normalise(Settings, _protect);
            Write(Settings);
        }

        Changed?.Invoke(this, Settings);
    }

    /// <summary>A deep copy, safe to read on another thread while the user keeps editing.</summary>
    public AppSettings Snapshot()
    {
        lock (_gate)
        {
            return JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(Settings, BoatdrillJson.Options), BoatdrillJson.Options)!;
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            // Tries again to encrypt what could not be encrypted before.
            Normalise(Settings, _protect);
            Write(Settings);
        }
    }

    private AppSettings Load()
    {
        if (File.Exists(FilePath))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), BoatdrillJson.Options);
                if (loaded is not null)
                {
                    // A password left in a URL by an earlier version is moved out here, and off the disk at the next save.
                    Normalise(loaded, _protect);
                    return loaded;
                }
            }
            catch (JsonException)
            {
                try
                {
                    File.Copy(FilePath, FilePath + ".broken", overwrite: true);
                }
                catch (IOException)
                {
                    // Keeping the broken copy is a courtesy; the defaults below still apply.
                }
            }
        }

        var defaults = _defaults();
        Normalise(defaults, _protect);
        Write(defaults);
        return defaults;
    }

    private void Write(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, BoatdrillJson.Indented));
        File.Move(temporary, FilePath, overwrite: true);
    }

    /// <summary>
    /// Fills in what a hand-edited file may lack and keeps numbers in a sensible range. With <paramref name="protect"/>,
    /// a password written into a destination's URL is moved out of it, encrypted (<see cref="DestinationSetting.ProtectLocation"/>).
    /// </summary>
    internal static void Normalise(AppSettings settings, Func<string, string?>? protect = null)
    {
        settings.Sources = (settings.Sources ?? [])
            .Where(s => s is not null && !string.IsNullOrWhiteSpace(s.Folder))
            .Select(s =>
            {
                if (string.IsNullOrWhiteSpace(s.Id)) s.Id = Source.IdForPath(s.Folder);
                if (string.IsNullOrWhiteSpace(s.Name)) s.Name = Source.DisplayName(s.Folder);
                s.Excludes ??= [];
                return s;
            })
            .DistinctBy(s => s.Id)
            .ToList();

        settings.Destinations = (settings.Destinations ?? [])
            .Where(d => d is not null && !string.IsNullOrWhiteSpace(d.Folder))
            .Select(d =>
            {
                // The id is taken from the location as typed, before any password is masked: the same id as the form gives it.
                if (string.IsNullOrWhiteSpace(d.Id)) d.Id = Destination.IdFor(d.Kind, d.Folder);
                if (string.IsNullOrWhiteSpace(d.Name)) d.Name = Destination.DefaultName(d.Kind, d.Folder);
                if (protect is not null) d.ProtectLocation(protect);
                return d;
            })
            .DistinctBy(d => d.Id)
            .ToList();

        settings.SampleSize = Math.Clamp(settings.SampleSize, 20, 5000);
        settings.ByteCapMb = Math.Clamp(settings.ByteCapMb, 16, 64 * 1024);
        if (!double.IsFinite(settings.WindowWidth) || settings.WindowWidth < 400) settings.WindowWidth = 1080;
        if (!double.IsFinite(settings.WindowHeight) || settings.WindowHeight < 300) settings.WindowHeight = 740;
        if (settings.UiLanguage is not (Text.Language.EnglishCode or Text.Language.KoreanCode)) settings.UiLanguage = string.Empty;
    }
}
