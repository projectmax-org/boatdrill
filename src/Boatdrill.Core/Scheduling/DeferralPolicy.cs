using Boatdrill.Core.Settings;

namespace Boatdrill.Core.Scheduling;

public enum DeferReason
{
    None,
    FullScreen,
    OnBattery,
    InUse,
}

/// <summary>What the PC is doing at the moment a scheduled drill falls due.</summary>
public sealed record SystemState(bool FullScreen, bool OnBattery, TimeSpan IdleFor);

/// <summary>
/// Decides whether a due drill runs now or waits. Checked once when the timer fires, never polled.
/// A game or presentation always wins. Waiting for mains power or for the user to step away stops
/// after a while, so a laptop that is never idle and never plugged in still gets its drill.
/// </summary>
public static class DeferralPolicy
{
    /// <summary>Keyboard and mouse untouched this long counts as away.</summary>
    public static readonly TimeSpan IdleThreshold = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan StopWaitingForIdleAfter = TimeSpan.FromDays(1);

    public static readonly TimeSpan StopWaitingForMainsAfter = TimeSpan.FromDays(3);

    /// <summary>A deferred drill is looked at again after this long.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(10);

    public static DeferReason Evaluate(SystemState state, AppSettings settings, TimeSpan overdue) =>
        Evaluate(state, settings.OnlyOnMainsPower, settings.OnlyWhenAway, overdue);

    public static DeferReason Evaluate(SystemState state, bool onlyOnMainsPower, bool onlyWhenAway, TimeSpan overdue)
    {
        if (state.FullScreen) return DeferReason.FullScreen;
        if (onlyOnMainsPower && state.OnBattery && overdue < StopWaitingForMainsAfter) return DeferReason.OnBattery;
        if (onlyWhenAway && state.IdleFor < IdleThreshold && overdue < StopWaitingForIdleAfter) return DeferReason.InUse;
        return DeferReason.None;
    }

    /// <summary>Completes "The next drill is due, ..." in the app's status line.</summary>
    public static string Describe(DeferReason reason) => reason switch
    {
        DeferReason.FullScreen => "waiting for the full-screen app to close",
        DeferReason.OnBattery => "waiting for mains power",
        DeferReason.InUse => "waiting until you step away",
        _ => string.Empty,
    };
}
