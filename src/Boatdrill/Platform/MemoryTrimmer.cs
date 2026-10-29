using System.Runtime;

namespace Boatdrill.Platform;

/// <summary>
/// Hands memory back to Windows when the app is about to sit quietly in the notification area: after the
/// window is hidden, and after a drill that ran with no window open. Pages fault back in on next use.
/// </summary>
public static class MemoryTrimmer
{
    public static void TrimNow()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            NativeMethods.SetProcessWorkingSetSizeEx(NativeMethods.GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1), 0);
        }
        catch (Exception ex)
        {
            Log.Warn("Memory trim failed.", ex);
        }
    }
}
