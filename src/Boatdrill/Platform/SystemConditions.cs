using System.Runtime.InteropServices;
using Boatdrill.Core.Scheduling;

namespace Boatdrill.Platform;

/// <summary>
/// What the PC is doing, read in an instant at the moment a scheduled drill falls due. Nothing here is
/// polled: an app that watched for games every second would be exactly what Boatdrill promises not to be.
/// </summary>
public static class SystemConditions
{
    public static SystemState Read() => new(IsFullScreenAppRunning(), IsOnBattery(), IdleTime());

    /// <summary>A game, a full-screen video or a presentation: the signal Windows uses to hold its own notifications.</summary>
    public static bool IsFullScreenAppRunning()
    {
        try
        {
            if (NativeMethods.SHQueryUserNotificationState(out var state) != 0) return false;
            return state is NativeMethods.QueryUserNotificationState.RunningDirect3DFullScreen
                        or NativeMethods.QueryUserNotificationState.PresentationMode
                        or NativeMethods.QueryUserNotificationState.Busy;
        }
        catch (Exception ex)
        {
            Log.Warn("Full-screen check failed.", ex);
            return false;
        }
    }

    public static bool IsOnBattery()
    {
        try
        {
            // ACLineStatus: 0 on battery, 1 on mains, 255 unknown (a desktop without a battery says 1).
            return NativeMethods.GetSystemPowerStatus(out var status) && status.ACLineStatus == 0;
        }
        catch (Exception ex)
        {
            Log.Warn("Power check failed.", ex);
            return false;
        }
    }

    /// <summary>How long the keyboard and mouse have been left alone in this session.</summary>
    public static TimeSpan IdleTime()
    {
        try
        {
            var info = new NativeMethods.LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.LASTINPUTINFO>() };
            if (!NativeMethods.GetLastInputInfo(ref info)) return TimeSpan.Zero;

            // Both counters wrap after 49.7 days; unsigned subtraction gives the right answer across a wrap.
            var idleMs = unchecked((uint)Environment.TickCount - info.dwTime);
            return TimeSpan.FromMilliseconds(idleMs);
        }
        catch (Exception ex)
        {
            Log.Warn("Idle check failed.", ex);
            return TimeSpan.Zero;
        }
    }
}
