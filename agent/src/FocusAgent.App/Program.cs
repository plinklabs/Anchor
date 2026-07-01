using FocusAgent.App.Extension;
using FocusAgent.App.Startup;
using FocusAgent.App.Tamper;
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
    /// Dev-only flag (#248): show the real last-resort <see cref="Diagnostics.CrashReportWindow"/>
    /// against a synthetic exception, with no WAM / hub / coordinator bootstrap, and
    /// keep it up so its surface (friendly headline, selectable detail field, the
    /// "Show technical details" expander, the magenta Copy button) can be
    /// screenshotted. Lets the crash dialog be verified end-to-end (build → launch →
    /// capture) without forcing a real crash. Used by the visual e2e
    /// (CrashDialogVisualTests) and scripts/dev. The window's composition is the
    /// production path; only the exception is synthetic.
    /// </summary>
    public const string ShowTestCrashArg = "--show-test-crash";

    /// <summary>
    /// Dev flag (#323): force the agent's UI language to a BCP-47 tag (e.g.
    /// <c>--ui-language nl-NL</c>) instead of following the Windows display
    /// language. Lets the Dutch localization be exercised end-to-end on an
    /// English box (a human running any surface, or a verify script) without
    /// changing the machine's display language. Off by default — the real agent
    /// passes nothing and follows the OS language with English fallback.
    /// </summary>
    public const string UiLanguageArg = "--ui-language";

    /// <summary>
    /// Dev-only flag (#323): resolve a representative set of localized strings for
    /// the language that follows (<c>--verify-i18n nl-NL</c>) — through both the
    /// real XAML <c>x:Uid</c> path (a live window) and the code-behind
    /// <see cref="Localization.Loc"/> path — write them to the result file named by
    /// <see cref="I18nResultPathEnvVar"/>, and exit. Drives the real resources.pri
    /// pipeline in the built exe so the integration test can assert Dutch renders
    /// and an unsupported language falls back to English. No WAM / hub / UI
    /// bootstrap beyond the one probe window. Mirrors <c>--verify-ds-theme</c>.
    /// </summary>
    public const string VerifyI18nArg = "--verify-i18n";

    /// <summary>
    /// Dev-only env var (#323): absolute path the <see cref="VerifyI18nArg"/> mode
    /// writes its <c>key=value</c> result lines to. The agent is a WinExe with no
    /// console, so the integration test reads the resolved strings from this file
    /// (same shape as the --verify-ds-theme / --check-update result files).
    /// </summary>
    public const string I18nResultPathEnvVar = "ANCHOR_I18N_RESULT_PATH";

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

    /// <summary>
    /// Dev-only flag (#225): write the per-user "start at login" entry under
    /// <c>HKCU\...\Run</c> pointing at the agent exe, print the resulting registry
    /// state, and exit — no WAM / hub / UI bootstrap. Drives the <em>real</em>
    /// registry-write path (the same one the Velopack install/update hook runs) so
    /// the integration test can assert the Run value is actually written on a clean
    /// box. Pair with <see cref="StartupRunKeyEnvVar"/> to point the write at a
    /// throwaway HKCU subtree instead of the live Run key.
    /// </summary>
    public const string RegisterStartupArg = "--register-startup";

    /// <summary>
    /// Dev-only flag (#225): the inverse of <see cref="RegisterStartupArg"/> —
    /// remove Anchor's Run entry (the uninstall path) and exit. Lets the integration
    /// test prove the entry is cleaned up on uninstall.
    /// </summary>
    public const string UnregisterStartupArg = "--unregister-startup";

    /// <summary>
    /// Dev-only env var (#225): an HKCU-relative key path that overrides the
    /// production <c>Run</c> key for <see cref="RegisterStartupArg"/> /
    /// <see cref="UnregisterStartupArg"/>, so the integration test writes to a
    /// throwaway subtree and never disturbs a dev's real auto-start entries.
    /// </summary>
    public const string StartupRunKeyEnvVar = "ANCHOR_STARTUP_RUN_KEY";

    /// <summary>
    /// Dev-only env var (#225): an absolute exe path the
    /// <see cref="RegisterStartupArg"/> mode registers instead of the running test
    /// exe's own path, so the integration test can assert the Run command points at
    /// a known, stable path of its choosing (rather than the agent's debug-bin path).
    /// </summary>
    public const string StartupExePathEnvVar = "ANCHOR_STARTUP_EXE_PATH";

    /// <summary>
    /// Dev-only flag (#288): register the witness native-messaging host — write the
    /// manifest + backend-url files next to the host exe and the HKCU
    /// <c>NativeMessagingHosts</c> key pointing at the manifest — print the resulting
    /// registry state, and exit (no WAM / hub / UI bootstrap). Drives the <em>real</em>
    /// registry + filesystem write path so the integration test can assert the
    /// registration is actually written on a clean box. Pair with
    /// <see cref="WitnessHostKeyEnvVar"/> / <see cref="WitnessHostExePathEnvVar"/> /
    /// <see cref="WitnessHostBackendUrlEnvVar"/> to point the write at a throwaway HKCU
    /// subtree and directory instead of the live Edge key.
    /// </summary>
    public const string RegisterWitnessHostArg = "--register-witness-host";

    /// <summary>
    /// Dev-only flag (#288): the inverse of <see cref="RegisterWitnessHostArg"/> —
    /// remove the witness host's HKCU key (the uninstall path) and exit. Lets the
    /// integration test prove the key is cleaned up on uninstall.
    /// </summary>
    public const string UnregisterWitnessHostArg = "--unregister-witness-host";

    /// <summary>
    /// Dev-only env var (#288): an HKCU-relative key path that overrides the production
    /// witness host key for <see cref="RegisterWitnessHostArg"/> /
    /// <see cref="UnregisterWitnessHostArg"/>, so the integration test writes to a
    /// throwaway subtree and never disturbs a dev's real native-messaging registration.
    /// </summary>
    public const string WitnessHostKeyEnvVar = "ANCHOR_WITNESS_HOST_KEY";

    /// <summary>
    /// Dev-only env var (#288): an absolute host exe path the
    /// <see cref="RegisterWitnessHostArg"/> mode registers instead of the agent's own
    /// <c>anchor-witness-host.exe</c>, so the integration test writes the manifest +
    /// backend-url files into a throwaway directory of its choosing (the exe need not
    /// exist — the CLI mode drives the registrar directly, without the startup
    /// path's exe-present guard).
    /// </summary>
    public const string WitnessHostExePathEnvVar = "ANCHOR_WITNESS_HOST_EXE_PATH";

    /// <summary>
    /// Dev-only env var (#288): the backend URL the <see cref="RegisterWitnessHostArg"/>
    /// mode bakes into <c>backend-url.json</c>, so the test can assert the configured
    /// backend reaches the file. Defaults to the local dev backend when unset.
    /// </summary>
    public const string WitnessHostBackendUrlEnvVar = "ANCHOR_WITNESS_HOST_BACKEND_URL";

    /// <summary>
    /// Dev-only flag (#224): run the real Velopack update <em>check</em> against a
    /// locally-served feed directory (a <c>vpk pack</c> output: RELEASES + the
    /// full/delta nupkg) instead of the live GitHub Releases feed, print the result,
    /// and exit — no WAM / hub / UI bootstrap, no actual download or apply. Drives
    /// the real <c>UpdateManager</c> + <c>SimpleFileSource</c> check path so the
    /// integration test can assert an installed agent discovers a newer release from
    /// a fake feed, without GitHub, admin, or a real install. The flag is followed
    /// by the feed directory path:
    /// <c>--check-update C:\path\to\feed</c>.
    /// </summary>
    public const string CheckUpdateArg = "--check-update";

    /// <summary>
    /// Dev-only env var (#224): the pretend "currently installed" version the
    /// <see cref="CheckUpdateArg"/> mode reports to Velopack, so the test can pin a
    /// version older than the one it packed into the feed and prove the check finds
    /// the newer release. Defaults to <c>0.0.1</c> if unset.
    /// </summary>
    public const string CheckUpdateCurrentVersionEnvVar = "ANCHOR_UPDATE_CURRENT_VERSION";

    /// <summary>
    /// Dev-only env var (#224): absolute path the <see cref="CheckUpdateArg"/> mode
    /// writes its one-line result to. The agent is a WinExe with no console
    /// attached, so the integration test reads the outcome from this file rather
    /// than stdout (the same shape as the --verify-ds-theme result file). Contents
    /// are <c>update-available: &lt;version&gt;</c>, <c>up-to-date</c>, or
    /// <c>error: &lt;message&gt;</c>.
    /// </summary>
    public const string CheckUpdateResultPathEnvVar = "ANCHOR_UPDATE_RESULT_PATH";

    /// <summary>
    /// The Velopack package id the agent ships under (mirrors
    /// <c>--packId</c> in agent/scripts/pack-release.ps1). The
    /// <see cref="CheckUpdateArg"/> mode reads the test feed as this app id.
    /// </summary>
    public const string VelopackAppId = "Anchor.Agent";

    public static bool ShowTestToast { get; private set; }
    public static bool ShowTestOverlay { get; private set; }
    public static bool ShowTestMainWindow { get; private set; }
    public static bool ShowTestJoinByCode { get; private set; }
    public static bool ShowTestTrayMenu { get; private set; }
    public static bool ShowTestGuidedInstall { get; private set; }
    public static bool VerifyDsTheme { get; private set; }
    public static bool ShowTestCrash { get; private set; }
    public static bool InjectToken { get; private set; }
    public static int? StatusEndpointPort { get; private set; }
    public static bool AutoJoin { get; private set; }
    public static bool SimulateInPrivate { get; private set; }

    /// <summary>The BCP-47 tag from <c>--ui-language</c>, or null to follow the OS language.</summary>
    public static string? UiLanguage { get; private set; }

    /// <summary>The BCP-47 tag from <c>--verify-i18n</c>, or null for a normal launch.</summary>
    public static string? VerifyI18nLanguage { get; private set; }

    /// <summary>Whether this launch is the dev-only <c>--verify-i18n</c> result-file mode.</summary>
    public static bool VerifyI18n => VerifyI18nLanguage is not null;

    /// <summary>
    /// Whether the last-resort crash dialog (#248) should be suppressed for this
    /// launch. A headless e2e / verify / dev self-test run intentionally exercises
    /// failure and must never block on a modal — there the fatal paths fall back to
    /// the breadcrumb log + a visible non-zero exit instead. The real, user-facing
    /// agent passes none of these flags, so it always gets the dialog.
    /// </summary>
    public static bool SuppressCrashDialog =>
        InjectToken || StatusEndpointPort is not null || AutoJoin || SimulateInPrivate ||
        ShowTestToast || ShowTestOverlay || ShowTestMainWindow || ShowTestJoinByCode ||
        ShowTestTrayMenu || ShowTestGuidedInstall || VerifyDsTheme || ShowTestCrash || VerifyI18n;

    [STAThread]
    public static int Main(string[] args)
    {
        // Velopack lifecycle hook (#209): MUST run before any UI. On an
        // install/update/uninstall launch Velopack injects hidden hook args; this
        // call handles them (e.g. (re)creating the Start-menu/Run-key shortcuts on
        // first run) and then exits the process, so the WinUI bootstrap below only
        // runs for a normal launch. `vpk pack` also refuses to package a build
        // whose entrypoint doesn't call this. The auto-update *check* (the agent's
        // UpdateManager against the GitHub Releases feed) is wired in App startup
        // via AgentUpdateService (#224).
        VelopackApp.Build()
            // #225: "start the agent at login" lives in a per-user HKCU\...\Run
            // entry (no admin, no MDM) — the unpackaged build's equivalent of a
            // packaged app's windows.startupTask extension. Velopack fires
            // OnAfterInstall on first install and
            // OnAfterUpdate after each update (a new versioned install dir), so the
            // agent (re-)points the Run value at the freshly-installed exe on both.
            // Idempotent — a re-run never leaks a duplicate or a stale path.
            .OnAfterInstallFastCallback(_ => StartupRegistration.RegisterForInstall())
            .OnAfterUpdateFastCallback(_ => StartupRegistration.RegisterForInstall())
            // #211 + #225: when the agent is uninstalled, un-pin the force-installed
            // extension and remove the Run entry it wrote — leaving the box as it
            // found it. Velopack fires this hook on the uninstall launch (before the
            // files go away). Best-effort: a failure here must not block uninstall.
            .OnBeforeUninstallFastCallback(_ =>
            {
                ExtensionRegistration.RemovePolicyForUninstall();
                StartupRegistration.RemoveForUninstall();
                // #288: un-register the witness native-messaging host (remove its HKCU
                // key) so uninstalling the agent leaves the box as it found it.
                WitnessHostRegistration.RemoveForUninstall();
            })
            .Run();

        // #211: dev-only register/unregister modes. These drive the real registry
        // write/remove and exit, so the integration test can assert the HKCU policy
        // value end-to-end. Handled before any WinUI bootstrap (and before single-
        // instance gating) since they neither show UI nor need a running agent.
        if (TryRunExtensionPolicyMode(args, out var exitCode))
            return exitCode;

        // #225: dev-only register/unregister startup modes. These drive the real
        // HKCU\...\Run write/remove (the same path the Velopack install/uninstall
        // hooks run) and exit, so the integration test can assert the Run entry
        // end-to-end. Handled before any WinUI bootstrap / single-instance gating
        // since they neither show UI nor need a running agent.
        if (TryRunStartupRegistrationMode(args, out exitCode))
            return exitCode;

        // #288: dev-only register/unregister witness-host modes. These drive the real
        // HKCU NativeMessagingHosts write/remove plus the manifest + backend-url file
        // writes (the same path the startup registrar and Velopack uninstall hook run)
        // and exit, so the integration test can assert the registration end-to-end.
        // Handled before any WinUI bootstrap / single-instance gating since they
        // neither show UI nor need a running agent.
        if (TryRunWitnessHostRegistrationMode(args, out exitCode))
            return exitCode;

        // #224: dev-only update-check mode. Drives the real Velopack check path
        // against a locally-served feed and exits, so the integration test can
        // assert an installed agent discovers a newer release from a fake feed.
        // Handled before any WinUI bootstrap / single-instance gating since it
        // shows no UI and needs no running agent.
        if (TryRunUpdateCheckMode(args, out exitCode))
            return exitCode;

        ShowTestToast = args.Any(a => string.Equals(a, ShowTestToastArg, StringComparison.OrdinalIgnoreCase));
        ShowTestOverlay = args.Any(a => string.Equals(a, ShowTestOverlayArg, StringComparison.OrdinalIgnoreCase));
        ShowTestMainWindow = args.Any(a => string.Equals(a, ShowTestMainWindowArg, StringComparison.OrdinalIgnoreCase));
        ShowTestJoinByCode = args.Any(a => string.Equals(a, ShowTestJoinByCodeArg, StringComparison.OrdinalIgnoreCase));
        ShowTestTrayMenu = args.Any(a => string.Equals(a, ShowTestTrayMenuArg, StringComparison.OrdinalIgnoreCase));
        ShowTestGuidedInstall = args.Any(a => string.Equals(a, ShowTestGuidedInstallArg, StringComparison.OrdinalIgnoreCase));
        VerifyDsTheme = args.Any(a => string.Equals(a, VerifyDsThemeArg, StringComparison.OrdinalIgnoreCase));
        ShowTestCrash = args.Any(a => string.Equals(a, ShowTestCrashArg, StringComparison.OrdinalIgnoreCase));
        InjectToken = args.Any(a => string.Equals(a, InjectTokenArg, StringComparison.OrdinalIgnoreCase));
        StatusEndpointPort = ParsePortAfter(args, StatusEndpointArg);
        AutoJoin = args.Any(a => string.Equals(a, AutoJoinArg, StringComparison.OrdinalIgnoreCase));
        SimulateInPrivate = args.Any(a => string.Equals(a, SimulateInPrivateArg, StringComparison.OrdinalIgnoreCase));
        UiLanguage = ArgValueAfter(args, UiLanguageArg);
        VerifyI18nLanguage = ArgValueAfter(args, VerifyI18nArg);

        // #323: pin the UI language before any XAML is parsed so x:Uid resources and
        // .NET formatting both resolve to it. --verify-i18n's language wins (that mode
        // exists precisely to render one language); otherwise the --ui-language flag,
        // else null → follow the Windows display language with English fallback. Set
        // here (before Application.Start / the WinAppSDK bootstrap) via system APIs
        // only; the MRT resource lookups happen later, once the runtime is up.
        Localization.Loc.SetStartupLanguage(VerifyI18nLanguage ?? UiLanguage);

        WinRT.ComWrappersSupport.InitializeComWrappers();

        // Single-instance gating gets in the way of the self-test loops (each
        // launch needs to be its own process). Skip it in those modes only.
        if (!ShowTestToast && !ShowTestOverlay && !ShowTestMainWindow && !ShowTestJoinByCode && !ShowTestTrayMenu && !ShowTestGuidedInstall && !VerifyDsTheme && !ShowTestCrash && !VerifyI18n)
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

    /// <summary>
    /// Handle the dev-only <c>--register-startup</c> / <c>--unregister-startup</c>
    /// modes (#225): perform the real HKCU\...\Run write/remove (against an optional
    /// throwaway key from <see cref="StartupRunKeyEnvVar"/>, and an optional exe path
    /// from <see cref="StartupExePathEnvVar"/>), print the resulting state for the
    /// harness to read, and signal the caller to exit. Returns false for a normal
    /// launch so <see cref="Main"/> proceeds to the WinUI bootstrap.
    /// </summary>
    private static bool TryRunStartupRegistrationMode(string[] args, out int exitCode)
    {
        exitCode = 0;
        var register = args.Any(a => string.Equals(a, RegisterStartupArg, StringComparison.OrdinalIgnoreCase));
        var unregister = args.Any(a => string.Equals(a, UnregisterStartupArg, StringComparison.OrdinalIgnoreCase));
        if (!register && !unregister) return false;

        var keyOverride = Environment.GetEnvironmentVariable(StartupRunKeyEnvVar);
        var exePathOverride = Environment.GetEnvironmentVariable(StartupExePathEnvVar);
        try
        {
            if (register)
                StartupRegistration.EnsureRegistered(keyOverride, exePathOverride);
            else
                StartupRegistration.RemoveRegistration(keyOverride);

            // Echo the resulting command so the harness can assert on stdout too.
            var command = StartupRegistration.ReadRegisteredCommand(keyOverride);
            if (command is not null)
                Console.WriteLine($"run-at-login: {command}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"startup-registration mode failed: {ex.Message}");
            exitCode = 1;
        }
        return true;
    }

    /// <summary>
    /// Handle the dev-only <c>--register-witness-host</c> /
    /// <c>--unregister-witness-host</c> modes (#288): perform the real HKCU
    /// <c>NativeMessagingHosts</c> write/remove plus the manifest + backend-url file
    /// writes (against an optional throwaway key from <see cref="WitnessHostKeyEnvVar"/>,
    /// host exe path from <see cref="WitnessHostExePathEnvVar"/>, and backend URL from
    /// <see cref="WitnessHostBackendUrlEnvVar"/>), print the resulting state for the
    /// harness to read, and signal the caller to exit. Returns false for a normal launch
    /// so <see cref="Main"/> proceeds to the WinUI bootstrap.
    /// </summary>
    private static bool TryRunWitnessHostRegistrationMode(string[] args, out int exitCode)
    {
        exitCode = 0;
        var register = args.Any(a => string.Equals(a, RegisterWitnessHostArg, StringComparison.OrdinalIgnoreCase));
        var unregister = args.Any(a => string.Equals(a, UnregisterWitnessHostArg, StringComparison.OrdinalIgnoreCase));
        if (!register && !unregister) return false;

        var keyOverride = Environment.GetEnvironmentVariable(WitnessHostKeyEnvVar);
        try
        {
            if (register)
            {
                var hostExePath = Environment.GetEnvironmentVariable(WitnessHostExePathEnvVar) is { Length: > 0 } p
                    ? p
                    : WitnessHostRegistration.DefaultHostExePath();
                // Matches BackendUrlConfig.DevFallbackUrl (the host's own dev fallback);
                // only ever used by the dev CLI test mode, which sets the env var anyway.
                var backendUrl = Environment.GetEnvironmentVariable(WitnessHostBackendUrlEnvVar) is { Length: > 0 } u
                    ? u
                    : "http://localhost:5276";
                WitnessHostRegistration.EnsureRegistered(keyOverride, hostExePath, backendUrl);
            }
            else
            {
                WitnessHostRegistration.RemoveRegistration(keyOverride);
            }

            // Echo the resulting registered manifest path so the harness can assert on stdout too.
            var manifestPath = WitnessHostRegistration.ReadRegisteredManifestPath(keyOverride);
            if (manifestPath is not null)
                Console.WriteLine($"witness-host: {manifestPath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"witness-host mode failed: {ex.Message}");
            exitCode = 1;
        }
        return true;
    }

    /// <summary>
    /// Handle the dev-only <c>--check-update &lt;feedDir&gt;</c> mode (#224): run the
    /// real Velopack check against a locally-served feed (no GitHub, no real
    /// install), print the outcome for the harness to read, and signal the caller to
    /// exit. Prints one of:
    ///   <c>update-available: &lt;version&gt;</c> when the feed has a newer release, or
    ///   <c>up-to-date</c> when it doesn't.
    /// Returns false for a normal launch so <see cref="Main"/> proceeds.
    /// </summary>
    private static bool TryRunUpdateCheckMode(string[] args, out int exitCode)
    {
        exitCode = 0;
        var feedDir = ArgValueAfter(args, CheckUpdateArg);
        if (feedDir is null) return false;

        var resultPath = Environment.GetEnvironmentVariable(CheckUpdateResultPathEnvVar);

        void WriteResult(string line)
        {
            if (string.IsNullOrEmpty(resultPath)) return;
            try { File.WriteAllText(resultPath, line); } catch { /* exit code is the fallback signal */ }
        }

        try
        {
            if (!Directory.Exists(feedDir))
            {
                WriteResult($"error: feed directory not found: {feedDir}");
                exitCode = 1;
                return true;
            }

            var currentVersion =
                Environment.GetEnvironmentVariable(CheckUpdateCurrentVersionEnvVar) is { Length: > 0 } v
                    ? v
                    : "0.0.1";

            var manager = Updates.VelopackUpdateManager.ForLocalFeed(
                feedDir,
                VelopackAppId,
                currentVersion,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<Updates.VelopackUpdateManager>.Instance);

            // IsInstalled is forced true by the TestVelopackLocator in ForLocalFeed,
            // so the check actually runs (the production gate is in AgentUpdateService).
            var result = manager.CheckForUpdateAsync().GetAwaiter().GetResult();
            WriteResult(result.IsUpdateAvailable
                ? $"update-available: {result.TargetVersion}"
                : "up-to-date");
        }
        catch (Exception ex)
        {
            WriteResult($"error: {ex.Message}");
            exitCode = 1;
        }
        return true;
    }

    /// <summary>Return the argument value immediately following <paramref name="flag"/>, or null.</summary>
    private static string? ArgValueAfter(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
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
