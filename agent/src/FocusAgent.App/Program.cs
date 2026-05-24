using FocusAgent.Core.Watchdog;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace FocusAgent.App;

public static class Program
{
    private const string SingleInstanceKey = "Anchor.FocusAgent.SingleInstance";

    // Held for the lifetime of the process so the Watchdog (issue #35) can
    // probe App presence via Mutex.TryOpenExisting. AppInstance.FindOrRegisterForKey
    // above handles our own single-instance gating, but it's a Windows App
    // SDK concept that another process can't easily query — a real named
    // kernel mutex can. Static field is intentional: the GC must not collect
    // the handle, and the kernel will release the object when the process
    // exits (clean or hard kill alike).
    private static Mutex? _appPresenceMutex;

    /// <summary>
    /// Dev-only flag: shows a fake join-confirmation toast immediately, with
    /// no WAM / hub / coordinator bootstrap, and exits after the countdown.
    /// Lets the WinUI toast path be verified end-to-end (build → launch →
    /// screenshot) without a working backend or interactive sign-in. Used by
    /// `scripts/dev/verify-toast.ps1` to validate the #41 fix.
    /// </summary>
    public const string ShowTestToastArg = "--show-test-toast";

    /// <summary>
    /// Dev-only flag (#33): shows the focus-enforcement overlay against a
    /// synthetic allowlist, with no WAM / hub / coordinator bootstrap, and
    /// exits after a short buffer. Used by <c>scripts/dev/verify-overlay.ps1</c>
    /// to verify the overlay's visual surface end-to-end without needing a
    /// running backend or a real off-list app to trigger enforcement.
    /// </summary>
    public const string ShowTestOverlayArg = "--show-test-overlay";

    /// <summary>
    /// Dev-only flag (#44): swap <c>WamTokenProvider</c> for
    /// <c>InjectedTokenProvider</c> so the agent skips interactive sign-in
    /// entirely and authenticates to the backend via the
    /// <c>X-Dev-Impersonate-Oid</c> header alone. Requires
    /// <c>Dev:ImpersonateOid</c> to be set. Used by the verify script and any
    /// other headless run that must not block on a WAM picker. Off by default
    /// — production never passes this.
    /// </summary>
    public const string InjectTokenArg = "--inject-token";

    /// <summary>
    /// Dev-only flag (#44): start an HTTP listener on
    /// <c>http://127.0.0.1:&lt;port&gt;/status</c> exposing the agent's current
    /// connection + session state as JSON. Lets headless verify scripts poll
    /// the agent's actual state instead of guessing from screenshots or logs.
    /// Loopback-only.
    /// </summary>
    public const string StatusEndpointArg = "--status-endpoint";

    public static bool ShowTestToast { get; private set; }
    public static bool ShowTestOverlay { get; private set; }
    public static bool InjectToken { get; private set; }
    public static int? StatusEndpointPort { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        ShowTestToast = args.Any(a => string.Equals(a, ShowTestToastArg, StringComparison.OrdinalIgnoreCase));
        ShowTestOverlay = args.Any(a => string.Equals(a, ShowTestOverlayArg, StringComparison.OrdinalIgnoreCase));
        InjectToken = args.Any(a => string.Equals(a, InjectTokenArg, StringComparison.OrdinalIgnoreCase));
        StatusEndpointPort = ParsePortAfter(args, StatusEndpointArg);

        WinRT.ComWrappersSupport.InitializeComWrappers();

        // Single-instance gating gets in the way of the self-test loops (each
        // launch needs to be its own process). Skip it in those modes only.
        if (!ShowTestToast && !ShowTestOverlay)
        {
            var keyInstance = AppInstance.FindOrRegisterForKey(SingleInstanceKey);
            if (!keyInstance.IsCurrent)
            {
                return 0;
            }

            // Watchdog presence beacon (#35). Take ownership only if no one
            // else holds it; if WaitOne(0) fails the App is somehow already
            // running despite the AppInstance check above — bail rather than
            // racing.
            _appPresenceMutex = new Mutex(initiallyOwned: false, AnchorWatchdogPaths.AppPresenceMutexName, out _);
            if (!_appPresenceMutex.WaitOne(0))
            {
                _appPresenceMutex.Dispose();
                _appPresenceMutex = null;
                return 0;
            }

            // The Watchdog uses quit.flag freshness to decide whether the
            // App's absence was intentional. Whenever the App starts, clear
            // any leftover flag so a future Quit can't be confused with this
            // session's startup. See AnchorWatchdogPaths.QuitFlagPath().
            TryDeleteQuitFlag();
        }

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });

        return 0;
    }

    /// <summary>
    /// Mark this exit as a deliberate user-initiated quit so the Watchdog
    /// (#35) does not bounce the App back up. Writes the sentinel BEFORE
    /// <see cref="Application.Exit"/> so we can't be killed mid-write.
    /// Failures are swallowed — at worst the Watchdog will relaunch us,
    /// which is annoying but not unsafe.
    /// </summary>
    public static void MarkCleanShutdown()
    {
        try
        {
            var path = AnchorWatchdogPaths.QuitFlagPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch
        {
            // best-effort sentinel; see XML doc
        }
    }

    private static void TryDeleteQuitFlag()
    {
        try
        {
            var path = AnchorWatchdogPaths.QuitFlagPath();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignore; a leftover stale flag is at worst a 10s window where
            // a freshly killed App won't relaunch — and the freshness check
            // in QuitFlagGate guards against ancient flags.
        }
    }

    private static int? ParsePortAfter(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i + 1], out var port) &&
                port is > 0 and < 65536)
            {
                return port;
            }
        }
        return null;
    }
}
