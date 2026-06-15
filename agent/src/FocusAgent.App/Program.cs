using FocusAgent.App.Extension;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Velopack;

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
    /// Dev-only flag (#173): show the real <see cref="MainWindow"/> against a
    /// synthetic "connected, in a focus session" state, with no WAM / hub /
    /// coordinator bootstrap, and keep it up so its redesigned ink surface can be
    /// screenshotted. Lets the AA1 MainWindow redesign be verified end-to-end
    /// (build → launch → capture) without a backend or interactive sign-in. Used
    /// by the visual e2e (MainWindowVisualTests) and scripts/dev verify scripts.
    /// </summary>
    public const string ShowTestMainWindowArg = "--show-test-mainwindow";

    /// <summary>
    /// Dev-only flag (#175): show the real <see cref="Sessions.JoinByCodeWindow"/>
    /// against a no-op join client, with no WAM / hub / coordinator bootstrap, and
    /// keep it up so its redesigned ink surface (Space Mono code field, the magenta
    /// JOIN spark) can be screenshotted. Lets the AA3 join-by-code redesign be
    /// verified end-to-end (build → launch → capture) without a backend or
    /// interactive sign-in. Used by the visual e2e (JoinByCodeVisualTests) and
    /// scripts/dev verify scripts.
    /// </summary>
    public const string ShowTestJoinByCodeArg = "--show-test-joinbycode";

    /// <summary>
    /// Dev-only flag (#176): show the real tray context menu (the AA4 brand-styled
    /// flyout — ink surface, Space Mono status, on-ink actions, the magenta spark)
    /// from a tiny host window, with no WAM / hub / coordinator bootstrap, and keep
    /// it open so its redesigned ink surface can be screenshotted. A tray
    /// <c>MenuFlyout</c> is a popup, not a window, and can't be reached by clicking
    /// the tray headlessly — so this self-test shows the very same menu the
    /// TrayIconHost builds (via the shared TrayMenu factory) through a host window.
    /// Used by the visual e2e (TrayMenuVisualTests) and scripts/dev/verify-traymenu.ps1.
    /// </summary>
    public const string ShowTestTrayMenuArg = "--show-test-traymenu";

    /// <summary>
    /// Dev-only flag (#211): show the real <see cref="Extension.GuidedInstallWindow"/>
    /// — the guided-install fallback shown when the per-user force-install policy
    /// doesn't take — with no WAM / hub / coordinator bootstrap, and keep it up so
    /// its ink surface can be screenshotted. The guided fallback is the primary path
    /// on a policy-locked box (where the HKCU <c>Software\Policies</c> subtree is
    /// ACL-restricted and the force-install write is denied), so it's a load-bearing
    /// user-visible surface worth observing directly. Used by the visual e2e
    /// (GuidedInstallVisualTests) and scripts/dev.
    /// </summary>
    public const string ShowTestGuidedInstallArg = "--show-test-guided-install";

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

    /// <summary>
    /// Dev-only flag (#211): write the per-user Edge force-install policy
    /// (<c>ExtensionInstallForcelist</c>) for Anchor's extension, print the
    /// resulting registry state, and exit — no WAM / hub / UI bootstrap. Drives the
    /// <em>real</em> registry-write path so the integration test can assert the
    /// HKCU value is actually written on a clean box (the one real-world
    /// uncertainty the issue flags). Pair with <see cref="ExtensionPolicyKeyEnvVar"/>
    /// to point the write at a throwaway HKCU subtree instead of the live Edge key.
    /// </summary>
    public const string RegisterExtensionArg = "--register-extension";

    /// <summary>
    /// Dev-only flag (#211): the inverse of <see cref="RegisterExtensionArg"/> —
    /// remove Anchor's force-install policy entry (the uninstall path) and exit.
    /// Lets the integration test prove the entry is cleaned up on uninstall.
    /// </summary>
    public const string UnregisterExtensionArg = "--unregister-extension";

    /// <summary>
    /// Dev-only env var (#211): an HKCU-relative key path that overrides the
    /// production forcelist key for <see cref="RegisterExtensionArg"/> /
    /// <see cref="UnregisterExtensionArg"/>, so the integration test writes to a
    /// throwaway subtree and never disturbs a dev's real Edge policy.
    /// </summary>
    public const string ExtensionPolicyKeyEnvVar = "ANCHOR_EXTENSION_POLICY_KEY";

    public static bool ShowTestToast { get; private set; }
    public static bool ShowTestOverlay { get; private set; }
    public static bool ShowTestMainWindow { get; private set; }
    public static bool ShowTestJoinByCode { get; private set; }
    public static bool ShowTestTrayMenu { get; private set; }
    public static bool ShowTestGuidedInstall { get; private set; }
    public static bool VerifyDsTheme { get; private set; }
    public static bool InjectToken { get; private set; }
    public static int? StatusEndpointPort { get; private set; }
    public static bool AutoJoin { get; private set; }
    public static bool SimulateInPrivate { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        // Velopack lifecycle hook (#209): MUST run before any UI. On an
        // install/update/uninstall launch Velopack injects hidden hook args; this
        // call handles them (e.g. (re)creating the Start-menu/Run-key shortcuts on
        // first run) and then exits the process, so the WinUI bootstrap below only
        // runs for a normal launch. `vpk pack` also refuses to package a build
        // whose entrypoint doesn't call this. The auto-update *check*
        // (UpdateManager against the GitHub Releases feed) is a tracked follow-up.
        VelopackApp.Build()
            // #211: un-pin the force-installed extension when the agent is
            // uninstalled. Velopack fires this hook on the uninstall launch
            // (before the files go away), so the agent removes the HKCU forcelist
            // entry it wrote — leaving the box as it found it. Best-effort: a
            // failure here must not block the uninstall.
            .OnBeforeUninstallFastCallback(_ => ExtensionRegistration.RemovePolicyForUninstall())
            .Run();

        // #211: dev-only register/unregister modes. These drive the real registry
        // write/remove and exit, so the integration test can assert the HKCU policy
        // value end-to-end. Handled before any WinUI bootstrap (and before single-
        // instance gating) since they neither show UI nor need a running agent.
        if (TryRunExtensionPolicyMode(args, out var exitCode))
            return exitCode;

        ShowTestToast = args.Any(a => string.Equals(a, ShowTestToastArg, StringComparison.OrdinalIgnoreCase));
        ShowTestOverlay = args.Any(a => string.Equals(a, ShowTestOverlayArg, StringComparison.OrdinalIgnoreCase));
        ShowTestMainWindow = args.Any(a => string.Equals(a, ShowTestMainWindowArg, StringComparison.OrdinalIgnoreCase));
        ShowTestJoinByCode = args.Any(a => string.Equals(a, ShowTestJoinByCodeArg, StringComparison.OrdinalIgnoreCase));
        ShowTestTrayMenu = args.Any(a => string.Equals(a, ShowTestTrayMenuArg, StringComparison.OrdinalIgnoreCase));
        ShowTestGuidedInstall = args.Any(a => string.Equals(a, ShowTestGuidedInstallArg, StringComparison.OrdinalIgnoreCase));
        VerifyDsTheme = args.Any(a => string.Equals(a, VerifyDsThemeArg, StringComparison.OrdinalIgnoreCase));
        InjectToken = args.Any(a => string.Equals(a, InjectTokenArg, StringComparison.OrdinalIgnoreCase));
        StatusEndpointPort = ParsePortAfter(args, StatusEndpointArg);
        AutoJoin = args.Any(a => string.Equals(a, AutoJoinArg, StringComparison.OrdinalIgnoreCase));
        SimulateInPrivate = args.Any(a => string.Equals(a, SimulateInPrivateArg, StringComparison.OrdinalIgnoreCase));

        WinRT.ComWrappersSupport.InitializeComWrappers();

        // Single-instance gating gets in the way of the self-test loops (each
        // launch needs to be its own process). Skip it in those modes only.
        if (!ShowTestToast && !ShowTestOverlay && !ShowTestMainWindow && !ShowTestJoinByCode && !ShowTestTrayMenu && !ShowTestGuidedInstall && !VerifyDsTheme)
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

    /// <summary>
    /// Handle the dev-only <c>--register-extension</c> / <c>--unregister-extension</c>
    /// modes (#211): perform the real HKCU forcelist write/remove (against an
    /// optional throwaway key from <see cref="ExtensionPolicyKeyEnvVar"/>), print the
    /// resulting state for the harness to read, and signal the caller to exit.
    /// Returns false (and leaves <paramref name="exitCode"/> unset) for a normal
    /// launch so <see cref="Main"/> proceeds to the WinUI bootstrap.
    /// </summary>
    private static bool TryRunExtensionPolicyMode(string[] args, out int exitCode)
    {
        exitCode = 0;
        var register = args.Any(a => string.Equals(a, RegisterExtensionArg, StringComparison.OrdinalIgnoreCase));
        var unregister = args.Any(a => string.Equals(a, UnregisterExtensionArg, StringComparison.OrdinalIgnoreCase));
        if (!register && !unregister) return false;

        var keyOverride = Environment.GetEnvironmentVariable(ExtensionPolicyKeyEnvVar);
        try
        {
            if (register)
                ExtensionRegistration.WriteForcelistPolicy(keyOverride);
            else
                ExtensionRegistration.RemoveForcelistPolicy(keyOverride);

            // Echo the resulting entries so the harness can assert on stdout too.
            foreach (var entry in ExtensionRegistration.ReadForcelistEntries(keyOverride))
                Console.WriteLine($"forcelist: {entry}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"extension-policy mode failed: {ex.Message}");
            exitCode = 1;
        }
        return true;
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
