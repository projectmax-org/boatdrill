using System.Text.Json;
using Boatdrill.Core.Settings;

namespace Boatdrill.Core.Tests;

public sealed class SettingsStoreTests
{
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
}
