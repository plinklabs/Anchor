using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace FocusAgent.App;

public static class Program
{
    private const string SingleInstanceKey = "Anchor.FocusAgent.SingleInstance";

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
    /// Dev-only flag (#164): verify the design-system WinUI binding actually
    /// merged into the agent's <c>App.xaml</c> at runtime — a brush from the
    /// merged dictionary resolves, the bundled font resource resolves, and the
    /// Anchor per-product accent override won over the binding's neutral default.
    /// No WAM / hub / coordinator bootstrap. Writes <c>ds-theme-result.txt</c>
    /// next to the exe, sets the process exit code (0 ok / 1 fail), and exits.
    /// Used by <c>scripts/dev/verify-ds-theme.ps1</c>. The binding's own
    /// <c>--smoke</c> already proves the fonts physically load; this asserts the
    /// agent-side wiring on top of it.
    /// </summary>
    public const string VerifyDsThemeArg = "--verify-ds-theme";

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

    /// <summary>
    /// Dev-only flag (#93): auto-confirm every join-confirmation instead of
    /// showing the WinUI toast, so a headless run actually <em>joins</em> the
    /// session (and so receives mid-session <c>SessionBundlesUpdated</c>
    /// pushes). Used by <c>scripts/dev/verify-bundle-switch.ps1</c> to observe
    /// the agent rebuild its allowlist when the teacher changes bundles. Off by
    /// default — production always shows the real toast.
    /// </summary>
    public const string AutoJoinArg = "--auto-join";

    /// <summary>
    /// Dev-only flag (#148): swap the real Edge-window scanner for
    /// <c>SimulatedInPrivateScanner</c>, which always reports one synthetic Edge
    /// InPrivate window. Lets the headless e2e drive the agent-side InPrivate
    /// witness end-to-end (detect → report → backend <c>TamperDetected</c>)
    /// without a real InPrivate browser window, the same way <c>--auto-join</c>
    /// stands in for the interactive join toast. Off by default — production
    /// always scans the live window list.
    /// </summary>
    public const string SimulateInPrivateArg = "--simulate-inprivate";

    public static bool ShowTestToast { get; private set; }
    public static bool ShowTestOverlay { get; private set; }
    public static bool VerifyDsTheme { get; private set; }
    public static bool InjectToken { get; private set; }
    public static int? StatusEndpointPort { get; private set; }
    public static bool AutoJoin { get; private set; }
    public static bool SimulateInPrivate { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        ShowTestToast = args.Any(a => string.Equals(a, ShowTestToastArg, StringComparison.OrdinalIgnoreCase));
        ShowTestOverlay = args.Any(a => string.Equals(a, ShowTestOverlayArg, StringComparison.OrdinalIgnoreCase));
        VerifyDsTheme = args.Any(a => string.Equals(a, VerifyDsThemeArg, StringComparison.OrdinalIgnoreCase));
        InjectToken = args.Any(a => string.Equals(a, InjectTokenArg, StringComparison.OrdinalIgnoreCase));
        StatusEndpointPort = ParsePortAfter(args, StatusEndpointArg);
        AutoJoin = args.Any(a => string.Equals(a, AutoJoinArg, StringComparison.OrdinalIgnoreCase));
        SimulateInPrivate = args.Any(a => string.Equals(a, SimulateInPrivateArg, StringComparison.OrdinalIgnoreCase));

        WinRT.ComWrappersSupport.InitializeComWrappers();

        // Single-instance gating gets in the way of the self-test loops (each
        // launch needs to be its own process). Skip it in those modes only.
        if (!ShowTestToast && !ShowTestOverlay && !VerifyDsTheme)
        {
            var keyInstance = AppInstance.FindOrRegisterForKey(SingleInstanceKey);
            if (!keyInstance.IsCurrent)
            {
                return 0;
            }
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
