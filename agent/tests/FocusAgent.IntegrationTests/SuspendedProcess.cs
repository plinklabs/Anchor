using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// Makes a real app stop responding (#451): every thread of its process is
/// suspended until <see cref="Dispose"/>, so its windows handle no messages, as a
/// hung app's do. For a spec whose app that is not responding must be one the
/// session allows, such as Notepad, which the test cannot freeze from inside the
/// way it freezes its own <see cref="OffListWindow"/>. Keep it short: Windows
/// replaces a window that has not responded for 5 s with a ghost window of its
/// own, which would be another window for the agent to judge.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class SuspendedProcess : IDisposable
{
    private IntPtr _handle;

    private SuspendedProcess(IntPtr handle) => _handle = handle;

    /// <summary>Suspend every thread of process <paramref name="processId"/>.</summary>
    public static SuspendedProcess Suspend(int processId)
    {
        var handle = OpenProcess(ProcessSuspendResume, false, processId);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException($"OpenProcess({processId}) failed ({Marshal.GetLastWin32Error()}).");
        var status = NtSuspendProcess(handle);
        if (status != 0)
        {
            CloseHandle(handle);
            throw new InvalidOperationException($"NtSuspendProcess({processId}) failed (0x{status:X8}).");
        }
        return new SuspendedProcess(handle);
    }

    /// <summary>Whether the app that shows <paramref name="hwnd"/> handles a message within a moment.</summary>
    public static bool IsResponding(IntPtr hwnd) =>
        SendMessageTimeoutW(hwnd, WmNull, IntPtr.Zero, IntPtr.Zero, SmtoNormal, 200, out _) != IntPtr.Zero;

    /// <summary>Let the process run again.</summary>
    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle == IntPtr.Zero) return;
        NtResumeProcess(handle);
        CloseHandle(handle);
    }

    private const uint ProcessSuspendResume = 0x0800;
    private const uint WmNull = 0x0000;
    private const uint SmtoNormal = 0x0000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(IntPtr processHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutW(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
}
