using Boatdrill.Core.Settings;
using Boatdrill.Core.Text;

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
/// Decides whether a due drill runs now or waits. Checked when the timer fires, and again every ten minutes
/// (<see cref="RetryAfter"/>) while the drill waits.
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

    /// <summary>
    /// Completes "The next drill is due, ..." in the app's status line; in Korean, a sentence of its own that
    /// follows "다음 복원 훈련 시간이 되었습니다".
    /// </summary>
    public static string Describe(DeferReason reason) => Language.IsKorean
        ? reason switch
        {
            DeferReason.FullScreen => "전체 화면 앱이 닫히기를 기다리고 있습니다",
            DeferReason.OnBattery => "전원이 연결되기를 기다리고 있습니다",
            DeferReason.InUse => "자리를 비우실 때까지 기다리고 있습니다",
            _ => string.Empty,
        }
        : reason switch
        {
            DeferReason.FullScreen => "waiting for the full-screen app to close",
            DeferReason.OnBattery => "waiting for mains power",
            DeferReason.InUse => "waiting until you step away",
            _ => string.Empty,
        };
}
