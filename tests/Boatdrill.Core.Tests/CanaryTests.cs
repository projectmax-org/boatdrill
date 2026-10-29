using Boatdrill.Core.Drill;
using Boatdrill.Core.Sampling;

namespace Boatdrill.Core.Tests;

public sealed class CanaryTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private static readonly SampleOptions Everything = new() { SampleSize = 1000, Seed = 1, ChurnWindow = TimeSpan.FromHours(-1), PerFolderFloor = 0 };

    private static async Task<DestinationResult> DrillAsync(TempTree source, TempTree mirror, TestClock clock, bool canaries = true)
    {
        using var scratch = new TempTree();
        var report = await new DrillRunner(clock: clock).RunAsync(new DrillPlan
        {
            Sources = [Source.FromPath(source.Root, "Docs")],
            Destinations = [Destination.MirrorFolder(mirror.Root, "Drive")],
            ScratchDirectory = scratch.Root,
            Sampling = Everything,
            UseCanaries = canaries,
        });
        return Assert.Single(report.Destinations);
    }

    [Fact]
    public void Stamps_AreKept_NewestFirst_InWholeSeconds()
    {
        using var source = new TempTree();
        for (var i = 0; i < 15; i++) Assert.True(Canary.Stamp(source.Root, Start.AddDays(i).AddMilliseconds(400)));

        var stamps = Canary.Read(source.Root);

        Assert.Equal(Canary.KeepStamps, stamps.Count);
        Assert.Equal(Start.AddDays(14), stamps[0]);
        Assert.Equal(Start.AddDays(3), stamps[^1]);
        Assert.StartsWith("Boatdrill writes this file", File.ReadAllText(Canary.PathIn(source.Root)));
        Assert.False(Canary.Stamp(source.At("not there"), Start));
    }

    [Fact]
    public void BehindSince_IsTheOldestStampTheCopyLacks_PastTheGrace()
    {
        var now = Start.AddDays(10);
        DateTimeOffset[] written = [Start.AddDays(9.5), Start.AddDays(8), Start.AddDays(7), Start.AddDays(6)];

        Assert.Null(Canary.BehindSince(written, [Start.AddDays(9.5), Start.AddDays(8)], now));
        Assert.Equal(Start.AddDays(7), Canary.BehindSince(written, [Start.AddDays(6)], now));
        Assert.Null(Canary.BehindSince(written, [Start.AddDays(8)], now)); // lacks only a stamp from twelve hours ago
        Assert.Equal(Start.AddDays(6), Canary.BehindSince(written, [], now));
        Assert.Null(Canary.BehindSince([], [], now));
    }

    [Fact]
    public void TheCanary_IsNeverSampled()
    {
        using var source = new TempTree();
        source.Write("a.txt", "a", Start);
        Canary.Stamp(source.Root, Start);
        source.Write("docs/Boatdrill canary.txt", "someone's own file of that name, deeper down", Start);

        var sample = new Sampler(Everything).Take(Source.FromPath(source.Root));

        Assert.Equal(2, sample.Population);
        Assert.DoesNotContain(sample.Files, f => f.RelativePath == Canary.FileName);
    }

    [Fact]
    public async Task AStalledBackup_IsCaughtByTheCanary_ThoughNoFileChanged()
    {
        using var source = new TempTree();
        using var mirror = new TempTree();
        for (var i = 0; i < 5; i++) source.Write($"docs/{i}.txt", $"document {i}", Start.AddDays(-30));
        source.MirrorTo(mirror.Root);
        var clock = new TestClock(Start);

        var first = await DrillAsync(source, mirror, clock);
        Assert.Null(first.CanaryCopiedUtc);
        Assert.Equal([Start], Canary.Read(source.Root));

        source.MirrorTo(mirror.Root);
        clock.Now = Start.AddDays(1);
        var second = await DrillAsync(source, mirror, clock);
        Assert.Equal(5, second.Sampled);
        Assert.Equal(Start, second.CanaryCopiedUtc);
        Assert.Null(second.CanaryBehindSince);
        Assert.Null(second.BehindSince);

        // The backup stops. Nothing in the folder changes, so only the canary can tell.
        clock.Now = Start.AddDays(4);
        var third = await DrillAsync(source, mirror, clock);
        Assert.Equal(5, third.Verified);
        Assert.Equal(Start.AddDays(1), third.CanaryBehindSince);
        Assert.Equal(Start.AddDays(1), third.BehindSince);
        Assert.Equal(TimeSpan.FromDays(3), third.Lag);
        Assert.True(third.Score.Freshness < 1);
        Assert.Contains("The newest canary stamp copied here is from", third.Sentence);
    }

    [Fact]
    public async Task ABackupWithoutTheCanary_SaysSo()
    {
        using var source = new TempTree();
        using var mirror = new TempTree();
        for (var i = 0; i < 3; i++) source.Write($"docs/{i}.txt", $"document {i}", Start.AddDays(-30));
        source.MirrorTo(mirror.Root);
        Canary.Stamp(source.Root, Start.AddDays(-3));

        var result = await DrillAsync(source, mirror, new TestClock(Start));

        Assert.Equal(Start.AddDays(-3), result.CanaryBehindSince);
        Assert.Contains("No copy of the canary file was found here.", result.Sentence);
    }

    [Fact]
    public async Task WithoutCanaries_NothingIsWritten()
    {
        using var source = new TempTree();
        using var mirror = new TempTree();
        source.Write("a.txt", "a", Start.AddDays(-3));
        source.MirrorTo(mirror.Root);

        var result = await DrillAsync(source, mirror, new TestClock(Start), canaries: false);

        Assert.Equal(1, result.Verified);
        Assert.False(File.Exists(Canary.PathIn(source.Root)));
    }
}
