using System.Security.AccessControl;
using System.Security.Principal;

namespace Boatdrill.Platform;

/// <summary>
/// Ensures only one copy of Boatdrill runs, and lets a second launch bring the first one forward, or (with
/// --quit, as the installer runs it) ask it to exit.
/// <para>
/// A mutex detects the duplicate and named events carry the "show yourself" and "exit" signals. This is
/// used rather than a window broadcast because the first instance usually has no visible window to find:
/// it is sitting in the notification area.
/// </para>
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\Boatdrill.SingleInstance";
    private const string SignalName = @"Local\Boatdrill.Activate";
    private const string QuitName = @"Local\Boatdrill.Quit";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _signal;
    private readonly EventWaitHandle _quit;
    private CancellationTokenSource? _listener;

    private SingleInstance(Mutex mutex, EventWaitHandle signal, EventWaitHandle quit, bool isFirst)
    {
        _mutex = mutex;
        _signal = signal;
        _quit = quit;
        IsFirstInstance = isFirst;
    }

    public bool IsFirstInstance { get; }

    /// <summary>Raised on a background thread when another launch asks this instance to show itself.</summary>
    public event EventHandler? ActivationRequested;

    /// <summary>Raised on a background thread when another launch asks this instance to exit.</summary>
    public event EventHandler? QuitRequested;

    public static SingleInstance Acquire()
    {
        // The mutex and the event are this user's, elevated or not. Left to the default, a copy run as
        // administrator would make them open to administrators alone, and a normal launch beside it
        // could then neither find it nor wake it. Nobody else is given anything.
        MutexSecurity? mutexSecurity = null;
        EventWaitHandleSecurity? signalSecurity = null;
        using (var identity = WindowsIdentity.GetCurrent())
        {
            if (identity.User is { } user)
            {
                mutexSecurity = new MutexSecurity();
                mutexSecurity.AddAccessRule(new MutexAccessRule(user, MutexRights.FullControl, AccessControlType.Allow));
                signalSecurity = new EventWaitHandleSecurity();
                signalSecurity.AddAccessRule(new EventWaitHandleAccessRule(user, EventWaitHandleRights.FullControl, AccessControlType.Allow));
            }
        }

        var mutex = MutexAcl.Create(initiallyOwned: true, MutexName, out bool createdNew, mutexSecurity);
        var signal = EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, SignalName, out _, signalSecurity);
        var quit = EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, QuitName, out _, signalSecurity);
        return new SingleInstance(mutex, signal, quit, createdNew);
    }

    /// <summary>Called by a duplicate launch to wake the running instance.</summary>
    public void SignalFirstInstance()
    {
        try { _signal.Set(); }
        catch (Exception ex) { Log.Warn("Could not signal the running instance.", ex); }
    }

    /// <summary>
    /// Called by a duplicate launch to make the running instance exit, as the installer does before it replaces
    /// or removes the app. True once it has gone (it releases the mutex as it exits), false if it is still
    /// running when <paramref name="wait"/> is up.
    /// </summary>
    public bool AskFirstInstanceToQuit(TimeSpan wait)
    {
        try
        {
            _quit.Set();
            if (!_mutex.WaitOne(wait)) return false;
        }
        catch (AbandonedMutexException)
        {
            // It ended without releasing the mutex: gone all the same, and the mutex is ours now.
        }
        catch (Exception ex)
        {
            Log.Warn("Could not ask the running instance to exit.", ex);
            return false;
        }

        _mutex.ReleaseMutex();
        return true;
    }

    /// <summary>Starts listening for activation and exit requests. Only the first instance should call this.</summary>
    public void StartListening()
    {
        if (!IsFirstInstance || _listener is not null) return;

        _listener = new CancellationTokenSource();
        var token = _listener.Token;

        var thread = new Thread(() =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // A timeout keeps the thread responsive to cancellation on shutdown.
                    switch (WaitHandle.WaitAny([_signal, _quit], TimeSpan.FromSeconds(1)))
                    {
                        case 0:
                            ActivationRequested?.Invoke(this, EventArgs.Empty);
                            break;
                        case 1:
                            QuitRequested?.Invoke(this, EventArgs.Empty);
                            return;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("Activation listener failed.", ex);
                    return;
                }
            }
        })
        {
            Name = "Boatdrill Activation",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };

        thread.Start();
    }

    public void Dispose()
    {
        _listener?.Cancel();
        _listener?.Dispose();

        try
        {
            if (IsFirstInstance) _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned; nothing to release.
        }

        _mutex.Dispose();
        _signal.Dispose();
        _quit.Dispose();
    }
}
