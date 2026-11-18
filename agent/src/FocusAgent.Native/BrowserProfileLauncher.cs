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
/// browser forward when it already runs). Given a URL, it opens that in the
/// profile too: the store listing that adds the extension to a school profile
/// without it (#536).
///
/// Only Edge is opened that way. A run that stands another process in for the
/// browser (the agent e2e plays Edge with its own windows) never starts it, so a
/// test can't open Edge, or anything else, on the developer's desktop; what it
/// would have opened is still kept (<see cref="LastOpened"/>).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BrowserProfileLauncher : IBrowserProfileLauncher
{
    private const string Edge = "msedge";

    private readonly string _browser;
    private readonly ILogger<BrowserProfileLauncher> _log;
    private BrowserProfileOpened? _lastOpened;

    /// <param name="browserProcessName">The browser as a process name (<c>msedge</c>).</param>
    public BrowserProfileLauncher(string browserProcessName, ILogger<BrowserProfileLauncher>? log = null)
    {
        var name = (browserProcessName ?? "").Trim();
        _browser = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        _log = log ?? NullLogger<BrowserProfileLauncher>.Instance;
    }

    /// <summary>
    /// The last profile (and URL) the agent asked to open, whether or not the
    /// browser was started for it; null before the first. Dev-only, for /status:
    /// lets the headless e2e, whose browser is a stand-in that is never started,
    /// see which profile the overlay's button opened what in (#536).
    /// </summary>
    public BrowserProfileOpened? LastOpened => Volatile.Read(ref _lastOpened);

    public void CloseWindow(nint windowHandle)
    {
        if (windowHandle == IntPtr.Zero) return;
        if (!NativeMethods.PostMessageW(windowHandle, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero))
            _log.LogWarning(
                "Asking window 0x{Window:X} to close failed (error {Error})",
                windowHandle, System.Runtime.InteropServices.Marshal.GetLastWin32Error());
    }

    public void OpenProfile(string profile, string? url = null)
    {
        var arguments = ArgumentsFor(profile, url);
        if (arguments is null)
        {
            _log.LogWarning("Not opening Edge in profile {Profile} at {Url}: not a profile folder or a web address", profile, url ?? "-");
            return;
        }
        Volatile.Write(ref _lastOpened, new BrowserProfileOpened(profile, url));
        if (!_browser.Equals(Edge, StringComparison.OrdinalIgnoreCase))
        {
            _log.LogInformation(
                "Not opening {Browser} in profile {Profile} at {Url}: only Edge is opened in a profile",
                _browser, profile, url ?? "-");
            return;
        }
        try
        {
            // App Paths resolves msedge.exe, as for any allowed app the overlay opens.
            using var process = Process.Start(new ProcessStartInfo($"{Edge}.exe")
            {
                UseShellExecute = true,
                Arguments = arguments,
            });
            _log.LogInformation("Opened Edge in profile {Profile} at {Url}", profile, url ?? "-");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Opening Edge in profile {Profile} failed", profile);
        }
    }

    /// <summary>
    /// Edge's command line for opening <paramref name="profile"/> (a profile folder,
    /// Edge's own name such as <c>Profile 1</c>), at <paramref name="url"/> when one
    /// is given: <c>--profile-directory="Profile 1" "https://…"</c>. Null when the
    /// folder is none (blank, or with a quote that would break out of its quotes) or
    /// the URL isn't an http(s) address that stays one argument.
    /// </summary>
    public static string? ArgumentsFor(string? profile, string? url)
    {
        if (string.IsNullOrWhiteSpace(profile) || profile.Contains('"'))
            return null;
        var arguments = $"--profile-directory=\"{profile}\"";
        if (url is null)
            return arguments;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || url.Any(c => c == '"' || char.IsWhiteSpace(c)))
            return null;
        return $"{arguments} \"{url}\"";
    }
}

/// <summary>What <see cref="BrowserProfileLauncher"/> was last asked to open (#536).</summary>
/// <param name="Profile">The profile folder.</param>
/// <param name="Url">The address opened in it, or null for the profile alone.</param>
public sealed record BrowserProfileOpened(string Profile, string? Url);
