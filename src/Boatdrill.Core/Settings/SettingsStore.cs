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

    public SettingsStore(string filePath, Func<AppSettings> defaults)
    {
        FilePath = filePath;
        _defaults = defaults;
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
            Normalise(Settings);
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
                    Normalise(loaded);
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
        Normalise(defaults);
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

    /// <summary>Fills in what a hand-edited file may lack and keeps numbers in a sensible range.</summary>
    internal static void Normalise(AppSettings settings)
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
                if (string.IsNullOrWhiteSpace(d.Id)) d.Id = Source.IdForPath(d.Folder);
                if (string.IsNullOrWhiteSpace(d.Name)) d.Name = Source.DisplayName(d.Folder);
                return d;
            })
            .DistinctBy(d => d.Id)
            .ToList();

        settings.SampleSize = Math.Clamp(settings.SampleSize, 20, 5000);
        settings.ByteCapMb = Math.Clamp(settings.ByteCapMb, 16, 64 * 1024);
        if (!double.IsFinite(settings.WindowWidth) || settings.WindowWidth < 400) settings.WindowWidth = 1080;
        if (!double.IsFinite(settings.WindowHeight) || settings.WindowHeight < 300) settings.WindowHeight = 740;
    }
}
