using System.Reflection;
using System.Runtime.InteropServices;
using FocusAgent.Core.Diagnostics;
using FocusAgent.Core.Logging;
using Microsoft.UI.Xaml;

namespace FocusAgent.App.Diagnostics;

/// <summary>
/// The agent's single "report fatal error" path (#248). Every fatal source funnels
/// here — the startup <c>try/catch</c> in <see cref="App.OnLaunched"/>, WinUI's
/// <see cref="Application.UnhandledException"/> (UI-thread exceptions), and
/// <see cref="AppDomain.UnhandledException"/> (background threads) — so a crash
/// surfaces a copyable dialog instead of the process vanishing silently (the #247
/// "opens and closes instantly").
///
/// It is deliberately self-contained: it must not touch the DI host, config, or
/// logging, because those are exactly what may have failed. It reuses only
/// <see cref="AgentLogPaths"/> (a pure path helper) and reads the version straight
/// off the assembly. Two surfaces:
/// <list type="bullet">
///   <item>a rich, selectable <see cref="CrashReportWindow"/> for the cases where
///   we own the UI thread and the message pump is alive (startup + UI-thread
///   handler);</item>
///   <item>a blocking Win32 <c>MessageBox</c> as the guaranteed-available fallback
///   — used on a terminating background thread (where spinning up a window is
///   unsafe) and whenever building the window itself throws.</item>
/// </list>
/// Both always <em>complement</em> the existing <c>startup-error.log</c> write,
/// never replace it.
/// </summary>
internal static class CrashReporter
{
    /// <summary>The exit code the process reports after a surfaced fatal error.</summary>
    private const int FatalExitCode = 1;

    // Guards against a crash *while reporting a crash* recursing forever (e.g. the
    // crash window's own construction throws). After the first report we only ever
    // append to the log.
    private static bool _reporting;

    /// <summary>
    /// Surface a fatal <em>startup</em> failure (the <see cref="App.OnLaunched"/>
    /// catch). Writes the log, then shows the crash window and keeps the message
    /// pump alive so the dialog stays interactive; the window's Close button exits
    /// the process. If the window can't be built, falls back to a blocking message
    /// box and exits. Caller must NOT rethrow afterwards — that would tear the
    /// process down under the dialog.
    /// </summary>
    public static void ReportStartupFatal(Application app, Exception ex) =>
        ReportOnUiThread(app, ex, "Anchor couldn't start");

    /// <summary>
    /// Surface a fatal UI-thread exception caught after startup (WinUI's
    /// <see cref="Application.UnhandledException"/>). Same surfacing as startup,
    /// with a headline that reflects the app was already running.
    /// </summary>
    public static void ReportUiThreadFatal(Application app, Exception ex) =>
        ReportOnUiThread(app, ex, "Anchor hit an error and needs to close");

    /// <summary>
    /// Surface a fatal exception on a background thread
    /// (<see cref="AppDomain.UnhandledException"/>), where the runtime is tearing
    /// the process down and spinning up a window is unsafe. Shows the blocking
    /// fallback box on the faulting thread so it appears before the CLR exits, then
    /// returns to let termination proceed.
    /// </summary>
    public static void ReportBackgroundFatal(Exception ex)
    {
        WriteLog(ex);
        if (_reporting) return;
        _reporting = true;
        ShowFallbackBox("Anchor hit an error and needs to close", ex);
    }

    /// <summary>
    /// Best-effort append to <c>startup-error.log</c> — the durable breadcrumb that
    /// the dialog complements. Always safe to call (swallows every failure) and is
    /// the one thing we still do even when crash dialogs are suppressed.
    /// </summary>
    public static void WriteLog(Exception ex)
    {
        try
        {
            var dir = AgentLogPaths.LocalAppDataLogDirectory();
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                LogFilePath(),
                $"{DateTimeOffset.Now:O}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // last-resort logger; intentionally swallow
        }
    }

    private static void ReportOnUiThread(Application app, Exception ex, string headline)
    {
        WriteLog(ex);
        if (_reporting) return;
        _reporting = true;

        try
        {
            var detail = CrashDiagnostics.BuildDetail(ex, AgentVersion(), LogFilePath());
            var fullStack = CrashDiagnostics.BuildFullStack(ex);

            var window = new CrashReportWindow(headline, detail, fullStack);
            // Closing the dialog ends the process — explicitly, with a non-zero exit
            // code, so a crash never looks like a clean exit regardless of whether
            // other windows are open.
            window.Closed += (_, _) => Environment.Exit(FatalExitCode);
            window.ConfigureAndShow();
            // Return without rethrowing: the activated window keeps the WinUI pump
            // alive, so the dialog stays up until the user closes it.
        }
        catch
        {
            // The window path failed (e.g. resources gone) — fall back to the
            // blocking box, then exit since nothing is keeping the pump alive.
            ShowFallbackBox(headline, ex);
            Environment.Exit(FatalExitCode);
        }
    }

    private static void ShowFallbackBox(string headline, Exception ex)
    {
        try
        {
            var detail = CrashDiagnostics.BuildDetail(ex, AgentVersion(), LogFilePath());
            var body = $"{headline}.{Environment.NewLine}{Environment.NewLine}{detail}";
            _ = MessageBoxW(IntPtr.Zero, body, "Anchor", MB_OK | MB_ICONERROR);
        }
        catch
        {
            // intentionally swallow — there is nothing further we can do
        }
    }

    private static string LogFilePath()
    {
        try
        {
            return Path.Combine(AgentLogPaths.LocalAppDataLogDirectory(), "startup-error.log");
        }
        catch
        {
            return "startup-error.log";
        }
    }

    private static string? AgentVersion()
    {
        try
        {
            var asm = typeof(CrashReporter).Assembly;
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            // InformationalVersion is "<VersionPrefix>" optionally plus a "+<sha>"
            // SourceLink suffix; show just the SemVer.
            if (!string.IsNullOrWhiteSpace(info)) return info.Split('+', 2)[0];
            return asm.GetName().Version?.ToString();
        }
        catch
        {
            return null;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private const uint MB_OK = 0x00000000;
    private const uint MB_ICONERROR = 0x00000010;
}
