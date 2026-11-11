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
/// <see cref="Program.LogDirectory"/> (a pure path helper over the launch's flags
/// and <see cref="AgentLogPaths.LogDirectoryEnvVar"/>, #463) and the version through
/// <see cref="AgentVersion"/>, a never-throwing read of the assembly's stamp (#423).
/// Two surfaces:
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
///
/// <para>
/// The startup and background paths also take the fatal-path update check (#407)
/// as a plain <see cref="Task"/> (no DI), and keep the process alive until it has
/// settled, at most <see cref="PendingUpdateGrace"/>. A student who closes the
/// dialog straight away then no longer kills the download of the fixed release.
/// </para>
/// </summary>
internal static class CrashReporter
{
    /// <summary>The exit code the process reports after a surfaced fatal error.</summary>
    private const int FatalExitCode = 1;

    /// <summary>
    /// How long a fatal path keeps the process alive, after its dialog is gone, for
    /// the update check to finish downloading (#407); the next launch applies what it
    /// downloaded (#449). Long enough for a check and a delta download on a school
    /// network; bounded because single-instance gating turns a relaunch into a no-op
    /// while this process lingers.
    /// </summary>
    internal static readonly TimeSpan PendingUpdateGrace = TimeSpan.FromSeconds(60);

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
    /// process down under the dialog. The exit waits for
    /// <paramref name="pendingUpdate"/> (#407), bounded.
    /// </summary>
    public static void ReportStartupFatal(Application app, Exception ex, Task? pendingUpdate = null) =>
        ReportOnUiThread(app, ex, "Anchor couldn't start", pendingUpdate);

    /// <summary>
    /// Surface a fatal UI-thread exception caught after startup (WinUI's
    /// <see cref="Application.UnhandledException"/>). Same surfacing as startup,
    /// with a headline that reflects the app was already running.
    /// </summary>
    public static void ReportUiThreadFatal(Application app, Exception ex) =>
        ReportOnUiThread(app, ex, "Anchor hit an error and needs to close", pendingUpdate: null);

    /// <summary>
    /// Surface a fatal exception on a background thread
    /// (<see cref="AppDomain.UnhandledException"/>), where the runtime is tearing
    /// the process down and spinning up a window is unsafe. Shows the blocking
    /// fallback box on the faulting thread so it appears before the CLR exits, waits
    /// (bounded) for <paramref name="pendingUpdate"/> (#407), then returns to let
    /// termination proceed.
    /// </summary>
    public static void ReportBackgroundFatal(Exception ex, Task? pendingUpdate = null)
    {
        WriteLog(ex);
        if (_reporting) return;
        _reporting = true;
        ShowFallbackBox("Anchor hit an error and needs to close", ex);
        // The runtime ends the process as soon as this handler returns.
        WaitForPendingUpdate(pendingUpdate);
    }

    /// <summary>
    /// Block until a fatal path's update check (#407) has settled, at most
    /// <see cref="PendingUpdateGrace"/>, so a fix it found is downloaded before the
    /// process exits and the next launch applies it. No-op for null; never throws.
    /// Safe on the UI thread: the check runs on the thread pool and never needs it.
    /// </summary>
    public static void WaitForPendingUpdate(Task? pendingUpdate)
    {
        if (pendingUpdate is null) return;
        try
        {
            pendingUpdate.Wait(PendingUpdateGrace);
        }
        catch
        {
            // Best-effort: an update failure must never change how we exit.
        }
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
            Directory.CreateDirectory(Program.LogDirectory);
            File.AppendAllText(
                LogFilePath(),
                $"{DateTimeOffset.Now:O}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // last-resort logger; intentionally swallow
        }
    }

    private static void ReportOnUiThread(Application app, Exception ex, string headline, Task? pendingUpdate)
    {
        WriteLog(ex);
        if (_reporting) return;
        _reporting = true;

        try
        {
            var detail = CrashDiagnostics.BuildDetail(ex, AgentVersion.Current, LogFilePath());
            var fullStack = CrashDiagnostics.BuildFullStack(ex);

            var window = new CrashReportWindow(headline, detail, fullStack);
            // #407: with an update still settling, closing the (last) window must not
            // let WinUI end the process on its own; ExitAfterPendingUpdate owns the
            // exit instead.
            if (pendingUpdate is not null)
                app.DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
            // Closing the dialog ends the process — explicitly, with a non-zero exit
            // code, so a crash never looks like a clean exit regardless of whether
            // other windows are open.
            window.Closed += (_, _) => ExitAfterPendingUpdate(pendingUpdate);
            window.ConfigureAndShow();
            // Return without rethrowing: the activated window keeps the WinUI pump
            // alive, so the dialog stays up until the user closes it.
        }
        catch
        {
            // The window path failed (e.g. resources gone) — fall back to the
            // blocking box, then exit since nothing is keeping the pump alive.
            ShowFallbackBox(headline, ex);
            WaitForPendingUpdate(pendingUpdate);
            Environment.Exit(FatalExitCode);
        }
    }

    /// <summary>
    /// The crash dialog's exit (#407): immediate when no update is settling;
    /// otherwise the dialog closes now (no frozen window) and the process exits from
    /// the thread pool once the update has settled, bounded. Exiting from the pool,
    /// not via the dispatcher, means a wedged UI thread can't keep the process (and
    /// its single-instance key) alive past the bound.
    /// </summary>
    private static void ExitAfterPendingUpdate(Task? pendingUpdate)
    {
        if (pendingUpdate is null || pendingUpdate.IsCompleted)
        {
            Environment.Exit(FatalExitCode);
            return;
        }

        _ = Task.Run(() =>
        {
            WaitForPendingUpdate(pendingUpdate);
            Environment.Exit(FatalExitCode);
        });
    }

    private static void ShowFallbackBox(string headline, Exception ex)
    {
        try
        {
            var detail = CrashDiagnostics.BuildDetail(ex, AgentVersion.Current, LogFilePath());
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
            return Path.Combine(Program.LogDirectory, "startup-error.log");
        }
        catch
        {
            return "startup-error.log";
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private const uint MB_OK = 0x00000000;
    private const uint MB_ICONERROR = 0x00000010;
}
