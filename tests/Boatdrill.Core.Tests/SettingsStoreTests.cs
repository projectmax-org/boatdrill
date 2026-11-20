using System.Text;
using System.Text.Json;
using Boatdrill.Core.Settings;

namespace Boatdrill.Core.Tests;

public sealed class SettingsStoreTests
{
    /// <summary>Stands in for DPAPI: what it stores can be turned back, but never reads as the text it was given.</summary>
    internal static string? Protect(string secret) => "test:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(secret));

    /// <summary>As DPAPI's, a value stored without the prefix is taken as it is.</summary>
    internal static string? Unprotect(string? stored) => stored is not null && stored.StartsWith("test:", StringComparison.Ordinal)
        ? Encoding.UTF8.GetString(Convert.FromBase64String(stored["test:".Length..]))
        : stored;

    [Fact]
    public void MissingFile_GetsTheDefaultsWrittenOut()
    {
        using var data = new TempTree();
        using var folders = new TempTree();
        var documents = Directory.CreateDirectory(folders.At("Documents")).FullName;
        var path = data.At("settings.json");

        var store = new SettingsStore(path, () => AppSettings.CreateDefault([documents, folders.At("Missing")]));

        var source = Assert.Single(store.Settings.Sources);
        Assert.Equal("Documents", source.Name);
        Assert.Contains("node_modules", source.Excludes);
        Assert.Empty(store.Settings.Destinations);
        Assert.False(store.Settings.IsReadyToDrill);
        Assert.True(File.Exists(path));
        Assert.DoesNotContain("isReadyToDrill", File.ReadAllText(path));
    }

    [Fact]
    public void DefaultFolders_TakeTheNamesWindowsShows_AndKeepTheirPaths()
    {
        using var folders = new TempTree();
        var documents = Directory.CreateDirectory(folders.At("Documents")).FullName;
        var pictures = Directory.CreateDirectory(folders.At("Pictures")).FullName;

        // Windows names Documents 문서 and says nothing about Pictures, which keeps its folder's name.
        var settings = AppSettings.CreateDefault([documents, pictures], folder => Path.GetFileName(folder) == "Documents" ? "문서" : null);

        Assert.Equal(["문서", "Pictures"], settings.Sources.Select(s => s.Name));
        Assert.Equal([Source.IdForPath(documents), Source.IdForPath(pictures)], settings.Sources.Select(s => s.Id));
        Assert.Equal([Path.GetFullPath(documents), Path.GetFullPath(pictures)], settings.Sources.Select(s => s.Folder));
    }

    [Fact]
    public void SavedNames_StayAsTheyWere()
    {
        using var data = new TempTree();
        using var folders = new TempTree();
        var documents = Directory.CreateDirectory(folders.At("Documents")).FullName;
        var path = data.At("settings.json");
        _ = new SettingsStore(path, () => AppSettings.CreateDefault([documents]));

        var reloaded = new SettingsStore(path, () => AppSettings.CreateDefault([documents], _ => "문서"));

        Assert.Equal("Documents", Assert.Single(reloaded.Settings.Sources).Name);
    }

    [Fact]
    public void Update_PersistsAndAnnounces()
    {
        using var data = new TempTree();
        var path = data.At("settings.json");
        var store = new SettingsStore(path, () => new AppSettings());
        AppSettings? announced = null;
        store.Changed += (_, s) => announced = s;

        store.Update(s =>
        {
            s.Destinations.Add(DestinationSetting.ForFolder(data.At("backup"), "Backup drive"));
            s.Frequency = DrillFrequency.Daily;
        });

        Assert.NotNull(announced);
        var reloaded = new SettingsStore(path, () => throw new InvalidOperationException("defaults must not be used"));
        var destination = Assert.Single(reloaded.Settings.Destinations);
        Assert.Equal("Backup drive", destination.Name);
        Assert.Equal(DrillFrequency.Daily, reloaded.Settings.Frequency);
        Assert.Contains("\"frequency\": \"Daily\"", File.ReadAllText(path));
    }

    [Fact]
    public void BrokenFile_IsKeptAsideAndDefaultsApply()
    {
        using var data = new TempTree();
        var path = data.Write("settings.json", "{ this is not json");

        var store = new SettingsStore(path, () => new AppSettings { SampleSize = 321 });

        Assert.Equal(321, store.Settings.SampleSize);
        Assert.Equal("{ this is not json", File.ReadAllText(path + ".broken"));
    }

    [Fact]
    public void HandEditedValues_AreRepaired()
    {
        using var data = new TempTree();
        var path = data.Write("settings.json", """
            {
              "sampleSize": 3,
              "byteCapMb": 999999999,
              "windowWidth": 10,
              "sources": [ { "folder": "/tmp/somewhere" }, { "folder": "" }, { "folder": "/tmp/somewhere" } ],
              "destinations": [ { "folder": "/mnt/backup", "name": "" } ]
            }
            """);

        var settings = new SettingsStore(path, () => new AppSettings()).Settings;

        Assert.Equal(20, settings.SampleSize);
        Assert.Equal(64 * 1024, settings.ByteCapMb);
        Assert.Equal(1080, settings.WindowWidth);
        var source = Assert.Single(settings.Sources);
        Assert.Equal("somewhere", source.Name);
        Assert.Equal(Source.IdForPath("/tmp/somewhere"), source.Id);
        Assert.Equal("backup", Assert.Single(settings.Destinations).Name);
    }

    [Fact]
    public void Snapshot_IsIndependentOfLaterEdits()
    {
        using var data = new TempTree();
        var store = new SettingsStore(data.At("settings.json"), () => new AppSettings());

        var snapshot = store.Snapshot();
        store.Update(s => s.Sources.Add(SourceSetting.ForFolder(data.Root)));

        Assert.Empty(snapshot.Sources);
        Assert.Single(store.Settings.Sources);
    }

    [Fact]
    public void Settings_ConvertToEngineTypes()
    {
        var source = new SourceSetting { Id = "s1", Name = "Docs", Folder = "/home/max/Documents", Excludes = ["*.tmp"] }.ToSource();
        var destination = new DestinationSetting { Id = "d1", Name = "Drive", Folder = "/mnt/e", Layout = MirrorLayout.RootFolder }.ToDestination();

        Assert.Equal(["*.tmp"], source.Excludes);
        Assert.Equal(MirrorLayout.RootFolder, destination.Layout);
        Assert.Equal(DestinationKind.MirrorFolder, destination.Kind);
        Assert.Equal(512L * 1024 * 1024, new AppSettings().ToSampleOptions().ByteCap);
        Assert.Equal("{}", JsonSerializer.Serialize(new { }));
    }

    [Theory]
    [InlineData("rest:https://max:hunter2@nas:8000/restic", "hunter2")]
    [InlineData("rest:https://max:p@ss:w$rd@nas/", "p@ss:w$rd")]
    [InlineData("rest:https://max:p%40ss@nas:8000/", "p%40ss")]
    public void APasswordInARepositoryUrl_IsSavedEncrypted_AndComesBackAsTyped(string url, string password)
    {
        using var data = new TempTree();
        var path = data.At("settings.json");
        var store = new SettingsStore(path, () => new AppSettings(), Protect);

        store.Update(s => s.Destinations.Add(DestinationSetting.ForRestic(url, "stored password")));

        Assert.DoesNotContain(password, File.ReadAllText(path));
        var saved = Assert.Single(new SettingsStore(path, () => throw new InvalidOperationException("defaults must not be used"), Protect).Settings.Destinations);
        Assert.Equal(Destination.Redact(url), saved.Folder);
        var destination = saved.ToDestination(Unprotect);
        Assert.Equal(url, destination.RootPath);
        Assert.Equal("stored password", destination.Secret);
        Assert.Equal(Destination.IdFor(DestinationKind.Restic, url), destination.Id);
        Assert.DoesNotContain(password, destination.Name);
        Assert.DoesNotContain(password, destination.DisplayLocation);

        // A drill reads a copy of the settings; without a way to decrypt, the location is the one shown, never the stored text.
        Assert.Equal(url, Assert.Single(store.Snapshot().Destinations).ToDestination(Unprotect).RootPath);
        Assert.Equal(Destination.Redact(url), saved.ToDestination().RootPath);
    }

    [Fact]
    public void AUrlSavedWithItsPassword_KeepsWorking_AndIsEncryptedAtTheNextSave()
    {
        using var data = new TempTree();
        const string url = "rest:https://max:hunter2@nas/";
        var id = Destination.IdFor(DestinationKind.Restic, url);
        // As an earlier version saved it, named by the end of the URL.
        var path = data.Write("settings.json", $$"""
            {
              "destinations": [ { "id": "{{id}}", "name": "hunter2@nas", "kind": "Restic", "folder": "{{url}}", "secret": "stored password" } ]
            }
            """);

        var store = new SettingsStore(path, () => throw new InvalidOperationException("defaults must not be used"), Protect);

        var loaded = Assert.Single(store.Settings.Destinations);
        Assert.Equal(url, loaded.ToDestination(Unprotect).RootPath);
        Assert.Equal(id, loaded.Id);
        Assert.Equal("nas", loaded.Name);

        store.Save();

        Assert.DoesNotContain("hunter2", File.ReadAllText(path));
        var saved = Assert.Single(new SettingsStore(path, () => new AppSettings(), Protect).Settings.Destinations);
        Assert.Equal(url, saved.ToDestination(Unprotect).RootPath);
        Assert.Equal(id, saved.Id);

        // A hand edit of the folder is not overruled by the URL kept for it.
        saved.Folder = "rest:https://max:***@nas2/";
        Assert.Equal("rest:https://max:***@nas2/", saved.ToDestination(Unprotect).RootPath);
    }

    [Fact]
    public void AUrlThatCannotBeEncrypted_IsSavedAsItWas_AndStillWorks()
    {
        using var data = new TempTree();
        const string url = "rest:https://max:hunter2@nas:8000/restic";
        var store = new SettingsStore(data.At("settings.json"), () => new AppSettings(), _ => null);

        store.Update(s => s.Destinations.Add(DestinationSetting.ForRestic(url, "stored password")));

        var saved = Assert.Single(store.Settings.Destinations);
        Assert.Equal(url, saved.Folder);
        Assert.Null(saved.ProtectedLocation);
        Assert.Equal(url, saved.ToDestination(Unprotect).RootPath);
    }
}
