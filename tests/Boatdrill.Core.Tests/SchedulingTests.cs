using Boatdrill.Core.Scheduling;
using Boatdrill.Core.Settings;

namespace Boatdrill.Core.Tests;

public sealed class SchedulingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NextDue_IsOneIntervalAfterTheLastDrill()
    {
        Assert.Equal(Now.AddDays(5), DrillSchedule.NextDue(DrillFrequency.Weekly, Now.AddDays(-2), Now));
        Assert.Equal(Now.AddDays(-1), DrillSchedule.NextDue(DrillFrequency.Daily, Now.AddDays(-2), Now));
        Assert.Equal(Now.AddDays(29), DrillSchedule.NextDue(DrillFrequency.Monthly, Now.AddDays(-1), Now));
    }

    [Fact]
    public void NextDue_WithoutHistory_IsShortlyAfterStartUp()
    {
        Assert.Equal(Now + DrillSchedule.FirstDrillDelay, DrillSchedule.NextDue(DrillFrequency.Weekly, null, Now));
    }

    [Fact]
    public void NextDue_OffMeansNever()
    {
        Assert.Null(DrillSchedule.NextDue(DrillFrequency.Off, Now.AddDays(-30), Now));
    }

    [Fact]
    public void NextDue_IgnoresALastDrillInTheFuture()
    {
        Assert.Equal(Now.AddDays(7), DrillSchedule.NextDue(DrillFrequency.Weekly, Now.AddYears(1), Now));
    }

    private static readonly SystemState Calm = new(FullScreen: false, OnBattery: false, IdleFor: TimeSpan.FromHours(1));

    [Fact]
    public void Deferral_RunsWhenAllIsCalm()
    {
        Assert.Equal(DeferReason.None, DeferralPolicy.Evaluate(Calm, true, true, TimeSpan.Zero));
    }

    [Fact]
    public void Deferral_NeverRunsDuringAFullScreenApp()
    {
        Assert.Equal(DeferReason.FullScreen, DeferralPolicy.Evaluate(Calm with { FullScreen = true }, false, false, TimeSpan.FromDays(60)));
    }

    [Fact]
    public void Deferral_WaitsForMainsPowerForAFewDays()
    {
        var onBattery = Calm with { OnBattery = true };

        Assert.Equal(DeferReason.OnBattery, DeferralPolicy.Evaluate(onBattery, true, true, TimeSpan.FromDays(2)));
        Assert.Equal(DeferReason.None, DeferralPolicy.Evaluate(onBattery, true, true, TimeSpan.FromDays(3)));
        Assert.Equal(DeferReason.None, DeferralPolicy.Evaluate(onBattery, false, true, TimeSpan.Zero));
    }

    [Fact]
    public void Deferral_WaitsForTheUserToStepAwayForADay()
    {
        var inUse = Calm with { IdleFor = TimeSpan.FromSeconds(30) };

        Assert.Equal(DeferReason.InUse, DeferralPolicy.Evaluate(inUse, true, true, TimeSpan.FromHours(23)));
        Assert.Equal(DeferReason.None, DeferralPolicy.Evaluate(inUse, true, true, TimeSpan.FromDays(1)));
        Assert.Equal(DeferReason.None, DeferralPolicy.Evaluate(inUse, true, false, TimeSpan.Zero));
        Assert.Equal(DeferReason.None, DeferralPolicy.Evaluate(Calm with { IdleFor = DeferralPolicy.IdleThreshold }, true, true, TimeSpan.Zero));
    }

    [Fact]
    public void Deferral_FollowsTheSettings()
    {
        var settings = new AppSettings { OnlyOnMainsPower = false, OnlyWhenAway = true };

        Assert.Equal(DeferReason.InUse, DeferralPolicy.Evaluate(Calm with { OnBattery = true, IdleFor = TimeSpan.Zero }, settings, TimeSpan.Zero));
        Assert.Equal("waiting until you step away", DeferralPolicy.Describe(DeferReason.InUse));
        Assert.Equal(string.Empty, DeferralPolicy.Describe(DeferReason.None));
    }
}
