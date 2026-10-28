using System.Runtime.Versioning;
using FocusAgent.Native.Win32;
using Microsoft.Extensions.Logging;

namespace FocusAgent.Native;

/// <summary>
/// Says when a window starts to minimize (<c>EVENT_SYSTEM_MINIMIZESTART</c>), so
/// <see cref="FocusEnforcer"/> knows when a window carried out the minimize a
/// hand-over asked of it (#474): <c>ShowWindowAsync</c> never says. Its WinEvent
/// hook is out of context, so it needs a thread that handles messages; it gets one
/// of its own, started the first time it is needed and kept for the life of the
/// process. Its own process's windows count too, so the enforcer's tests can use
/// windows of their own.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class MinimizeStartWatcher
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(2);

    private readonly Action<nint> _minimizing;
    private readonly ILogger _log;
    private readonly object _gate = new();
    // Held for as long as the hook may call it.
    private readonly NativeMethods.WinEventDelegate _callback;
    private bool _started;
    private volatile bool _watching;

    public MinimizeStartWatcher(Action<nint> minimizing, ILogger log)
    {
        _minimizing = minimizing;
        _log = log;
        _callback = OnWinEvent;
    }

    /// <summary>
    /// Starts watching, once; returns whether the hook is in place, so that a
    /// window's minimize is known when it comes.
    /// </summary>
    public bool EnsureStarted()
    {
        ManualResetEventSlim? ready = null;
        lock (_gate)
        {
            if (!_started)
            {
                _started = true;
                ready = new ManualResetEventSlim();
                var signal = ready;
                new Thread(() => Run(signal)) { IsBackground = true, Name = "minimize-start-watcher" }.Start();
            }
        }
        // Not disposed: the thread sets it even when the wait gave up on it.
        ready?.Wait(StartTimeout);
        return _watching;
    }

    private void Run(ManualResetEventSlim ready)
    {
        nint hook;
        try
        {
            hook = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_SYSTEM_MINIMIZESTART,
                NativeMethods.EVENT_SYSTEM_MINIMIZESTART,
                hmodWinEventProc: IntPtr.Zero,
                _callback,
                idProcess: 0,
                idThread: 0,
                NativeMethods.WINEVENT_OUTOFCONTEXT);
            _watching = hook != IntPtr.Zero;
            if (!_watching)
                _log.LogWarning("SetWinEventHook(MINIMIZESTART) returned NULL; a hand-over cannot tell when a window went down");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "SetWinEventHook(MINIMIZESTART) threw; a hand-over cannot tell when a window went down");
            return;
        }
        finally
        {
            ready.Set();
        }
        if (!_watching)
            return;

        // The hook's callbacks come in while the thread waits for a message.
        while (NativeMethods.GetMessageW(out _, IntPtr.Zero, 0, 0) > 0)
        {
        }
        _watching = false;
        NativeMethods.UnhookWinEvent(hook);
    }

    private void OnWinEvent(nint hWinEventHook, uint eventType, nint hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero)
            return;
        try
        {
            _minimizing(hwnd);
        }
        catch (Exception ex)
        {
            // Never throw out of the hook callback.
            _log.LogError(ex, "Handling the minimize of 0x{Hwnd:X} threw", hwnd);
        }
    }
}
