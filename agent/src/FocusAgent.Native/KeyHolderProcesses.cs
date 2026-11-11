using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FocusAgent.Core.Startup;
using FocusAgent.Native.Win32;

namespace FocusAgent.Native;

/// <summary>
/// The Win32 side of <see cref="SingleInstanceClaim"/> (#461): tells a running key
/// holder from one that is exiting by its exit code, and waits on its process handle,
/// which Windows signals only once every thread of it has exited (so its key is free).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class KeyHolderProcesses : IKeyHolderProcesses
{
    public KeyHolderState StateOf(uint processId)
    {
        using var process = NativeMethods.OpenProcessHandle(
            NativeMethods.SYNCHRONIZE | NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process.IsInvalid)
        {
            // Another user's process can't be opened; leave it be, as before. Any
            // other failure means no such process any more.
            return Marshal.GetLastWin32Error() == NativeMethods.ERROR_ACCESS_DENIED
                ? KeyHolderState.Running
                : KeyHolderState.Gone;
        }

        if (NativeMethods.WaitForSingleObject(process, 0) == NativeMethods.WAIT_OBJECT_0)
            return KeyHolderState.Gone;

        // Windows sets the exit code as soon as a process is ended (TerminateProcess
        // sets it before its threads have gone), so an exit code means on its way out.
        // Can't read it: take it for running, as before.
        if (!NativeMethods.GetExitCodeProcess(process, out var exitCode))
            return KeyHolderState.Running;
        return exitCode == NativeMethods.STILL_ACTIVE ? KeyHolderState.Running : KeyHolderState.Exiting;
    }

    public bool WaitUntilGone(uint processId, TimeSpan timeout)
    {
        using var process = NativeMethods.OpenProcessHandle(NativeMethods.SYNCHRONIZE, false, processId);
        if (process.IsInvalid)
            return Marshal.GetLastWin32Error() != NativeMethods.ERROR_ACCESS_DENIED;

        var ms = (uint)Math.Clamp(timeout.TotalMilliseconds, 0, uint.MaxValue - 1);
        return NativeMethods.WaitForSingleObject(process, ms) == NativeMethods.WAIT_OBJECT_0;
    }
}
