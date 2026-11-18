using System.Diagnostics;
using System.Runtime.Versioning;
using FocusAgent.Core.Tamper;
using FocusAgent.Native.Win32;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FocusAgent.Native;

/// <summary>
/// Takes a student from a paused Edge window to their school profile (#525):
/// closes the window the way its close button does, so Edge asks about its tabs
/// as it always does, and opens Edge in the school profile with
/// <c>--profile-directory</c>, which opens a window of that profile (or brings the
/// browser forward when it already runs).
///
/// Only Edge is opened that way. A run that stands another process in for the
/// browser (the agent e2e plays Edge with its own windows) never starts it, so a
/// test can't open Edge, or anything else, on the developer's desktop.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BrowserProfileLauncher : IBrowserProfileLauncher
{
    private const string Edge = "msedge";

    private readonly string _browser;
    private readonly ILogger<BrowserProfileLauncher> _log;

    /// <param name="browserProcessName">The browser as a process name (<c>msedge</c>).</param>
    public BrowserProfileLauncher(string browserProcessName, ILogger<BrowserProfileLauncher>? log = null)
    {
        var name = (browserProcessName ?? "").Trim();
        _browser = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        _log = log ?? NullLogger<BrowserProfileLauncher>.Instance;
    }

    public void CloseWindow(nint windowHandle)
    {
        if (windowHandle == IntPtr.Zero) return;
        if (!NativeMethods.PostMessageW(windowHandle, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero))
            _log.LogWarning(
                "Asking window 0x{Window:X} to close failed (error {Error})",
                windowHandle, System.Runtime.InteropServices.Marshal.GetLastWin32Error());
    }

    public void OpenProfile(string profile)
    {
        if (!_browser.Equals(Edge, StringComparison.OrdinalIgnoreCase))
        {
            _log.LogInformation("Not opening {Browser} in profile {Profile}: only Edge is opened in a profile", _browser, profile);
            return;
        }
        // A profile folder is Edge's own name ("Profile 1"); one with a quote in it
        // is none, and must not reach the command line.
        if (string.IsNullOrWhiteSpace(profile) || profile.Contains('"'))
        {
            _log.LogWarning("Not opening Edge in profile {Profile}: not a profile folder", profile);
            return;
        }
        try
        {
            // App Paths resolves msedge.exe, as for any allowed app the overlay opens.
            using var process = Process.Start(new ProcessStartInfo($"{Edge}.exe")
            {
                UseShellExecute = true,
                Arguments = $"--profile-directory=\"{profile}\"",
            });
            _log.LogInformation("Opened Edge in profile {Profile}", profile);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Opening Edge in profile {Profile} failed", profile);
        }
    }
}
