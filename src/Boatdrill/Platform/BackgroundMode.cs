namespace Boatdrill.Platform;

/// <summary>
/// Windows' background processing mode: lower CPU, disk and memory priority for the whole process while a
/// scheduled drill reads a backup, so a drill that starts as someone comes back to the PC stays out of their way.
/// </summary>
public static class BackgroundMode
{
    public static bool Enter()
    {
        try
        {
            return NativeMethods.SetPriorityClass(NativeMethods.GetCurrentProcess(), NativeMethods.PROCESS_MODE_BACKGROUND_BEGIN);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not enter background mode.", ex);
            return false;
        }
    }

    public static void Leave()
    {
        try
        {
            NativeMethods.SetPriorityClass(NativeMethods.GetCurrentProcess(), NativeMethods.PROCESS_MODE_BACKGROUND_END);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not leave background mode.", ex);
        }
    }
}
