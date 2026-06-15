using FocusAgent.App.Auth;
using FocusAgent.App.Connectivity;
using FocusAgent.App.Extension;
using FocusAgent.App.Focus;
using FocusAgent.App.Realtime;
using FocusAgent.App.Sessions;
using FocusAgent.App.Tamper;
using FocusAgent.App.Tray;
using FocusAgent.Core.Auth;
using FocusAgent.Core.Dtos;
using FocusAgent.Core.Extension;
using FocusAgent.Core.Focus;
using FocusAgent.Core.Logging;
using FocusAgent.Core.Realtime;
using FocusAgent.Core.Sessions;
using FocusAgent.Core.Settings;
using FocusAgent.Core.Tamper;
using FocusAgent.Native;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Serilog;
using Windows.UI;
using WinRT.Interop;

namespace FocusAgent.App;

public partial class App : Application
{
    private IHost? _host;
    private MainWindow? _mainWindow;
    private TrayIconHost? _tray;
    private SessionCoordinator? _coordinator;
    private SessionHeartbeatService? _heartbeat;
    private ExtensionWitnessMonitor? _witnessMonitor;
    private ExtensionSelfRegistrar? _extensionRegistrar;
    private InPrivateWitnessMonitor? _inPrivateMonitor;
    private SessionRehydrationService? _rehydration;
    private FocusSessionController? _focus;
    private ISessionHubConnection? _hub;
    private ConnectionManager? _connection;
    private StatusEndpoint? _statusEndpoint;
    private JoinByCodeFlow? _joinByCodeFlow;
    // Held only by the --show-test-toast path so the logger outlives the
    // async show/decide chain rather than getting disposed at OnLaunched return.
    private ILoggerFactory? _testLoggerFactory;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (Program.ShowTestToast)
        {
            RunToastSelfTest();
            return;
        }

        if (Program.ShowTestOverlay)
        {
            RunOverlaySelfTest();
            return;
        }

        if (Program.ShowTestMainWindow)
        {
            RunMainWindowSelfTest();
            return;
        }

        if (Program.ShowTestJoinByCode)
        {
            RunJoinByCodeSelfTest();
            return;
        }

        if (Program.ShowTestTrayMenu)
        {
            RunTrayMenuSelfTest();
            return;
        }

        if (Program.ShowTestGuidedInstall)
        {
            RunGuidedInstallSelfTest();
            return;
        }

        if (Program.VerifyDsTheme)
        {
            RunDsThemeVerification();
            return;
        }

        try
        {
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            _host = BuildHost(dispatcher, () => _mainWindow is null ? IntPtr.Zero : WindowNative.GetWindowHandle(_mainWindow));
            _host.Start();

            var logger = _host.Services.GetRequiredService<ILogger<App>>();
            logger.LogInformation("FocusAgent starting (unpackaged WinUI 3)");

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                logger.LogCritical(e.ExceptionObject as Exception, "Unhandled exception");
            TaskScheduler.UnobservedTaskException += (_, e) =>
                logger.LogError(e.Exception, "Unobserved task exception");

            _hub = _host.Services.GetRequiredService<ISessionHubConnection>();
            _coordinator = _host.Services.GetRequiredService<SessionCoordinator>();
            // Resolve eagerly so the heartbeat service's constructor wires up
            // its SessionJoined / SessionLeft subscriptions before the first
            // SessionStarted broadcast can possibly arrive.
            _heartbeat = _host.Services.GetRequiredService<SessionHeartbeatService>();
            // Resolve + start the extension witness eagerly (#146 part 1) so the
            // named-pipe server is listening before the native host can connect,
            // and so its SessionCoordinator-backed gate is wired before the first
            // session. Reports only fire on a drop during a joined session.
            _witnessMonitor = _host.Services.GetRequiredService<ExtensionWitnessMonitor>();
            _ = _witnessMonitor.StartAsync();
            // #211: self-register the Edge extension. Write the per-user force-
            // install policy, then (after the witness pipe is listening above so the
            // check-in can be observed) wait the grace period and, if the extension
            // never checks in, open the guided install. Fire-and-forget: the grace
            // wait must never block startup, and a failure can only fall back to the
            // guided window, never crash the agent.
            _extensionRegistrar = _host.Services.GetRequiredService<ExtensionSelfRegistrar>();
            _ = _extensionRegistrar.RegisterAndVerifyAsync();
            // Resolve the InPrivate witness eagerly (#148) so its SessionJoined /
            // SessionLeft subscriptions are wired before the first session — the
            // poll loop starts on join and reports any open Edge InPrivate window.
            _inPrivateMonitor = _host.Services.GetRequiredService<InPrivateWitnessMonitor>();
            // Also resolve the rehydration service eagerly so it's ready when
            // the connection manager fires its first Connected event below.
            _rehydration = _host.Services.GetRequiredService<SessionRehydrationService>();
            _focus = _host.Services.GetRequiredService<FocusSessionController>();
            _connection = _host.Services.GetRequiredService<ConnectionManager>();

            _mainWindow = new MainWindow(
                _connection,
                _coordinator,
                _heartbeat,
                _host.Services.GetRequiredService<IOptions<SessionSettings>>());
            _joinByCodeFlow = _host.Services.GetRequiredService<JoinByCodeFlow>();
            _tray = new TrayIconHost(
                onOpen: () => ShowMainWindow(),
                onJoinByCode: () => _joinByCodeFlow.Open(),
                // Per #34: the menu item is disabled while the agent is
                // already in (or being walked into) a session — no point
                // letting the student type a code they can't act on.
                canJoinByCode: () => _coordinator.ActiveSessionId is null && _coordinator.JoinedSessionId is null,
                onQuit: ShutdownCleanly,
                dispatcher: dispatcher);
            _tray.Show();

            _connection.StatusChanged += OnConnectionStatusChanged;
            _ = _connection.StartAsync();

            // Start the loopback status endpoint if requested (#44). Lets verify
            // scripts poll the agent's actual state (connection status + active
            // session id + joined session id) instead of guessing from
            // screenshots. Off by default.
            if (Program.StatusEndpointPort is int port)
            {
                _statusEndpoint = new StatusEndpoint(
                    _connection,
                    _coordinator,
                    _focus,
                    _inPrivateMonitor,
                    _host.Services.GetRequiredService<ILogger<StatusEndpoint>>(),
                    // #102: let the headless e2e drive the two new UI actions —
                    // leaving a session and closing the window to the tray —
                    // without UI automation. Loopback + dev-only, like /status.
                    onLeaveSession: ct => _coordinator.LeaveSessionManuallyAsync(ct),
                    onCloseWindow: () => _mainWindow?.DispatcherQueue.TryEnqueue(
                        () => _mainWindow!.HideToTray()),
                    // #110: drive tray → Quit headlessly. ShutdownCleanly touches
                    // the window and Application.Exit, so it must run on the UI
                    // thread — same marshalling as onCloseWindow above.
                    onQuit: () => _mainWindow?.DispatcherQueue.TryEnqueue(ShutdownCleanly));
                _statusEndpoint.Start(port);
            }
        }
        catch (Exception ex)
        {
            WriteStartupFailure(ex);
            throw;
        }
    }

    private void OnConnectionStatusChanged(object? sender, ConnectionStatusSnapshot snapshot)
    {
        // Mirror the manager's status into the tray text.
        _tray?.UpdateStatus(MapToTrayState(snapshot.Status), snapshot.DisplayName);

        // Surface stuck states by auto-opening MainWindow so the recovery
        // button is right in front of the user. SignInFailed is immediate
        // (no automatic retry will help); Disconnected gets a short grace
        // period so transient drops don't pop a window.
        switch (snapshot.Status)
        {
            case ConnectionStatus.SignInFailed:
                _mainWindow?.DispatcherQueue.TryEnqueue(ShowMainWindow);
                break;
            case ConnectionStatus.Disconnected:
                _ = AutoOpenOnSustainedDisconnectedAsync();
                break;
            case ConnectionStatus.Connected:
                _stuckSince = null;
                // Issue #54: on first Connected, ask the backend whether this
                // student is still mid-session and rejoin silently. The service
                // gates itself to run once per process; later Connected events
                // (reconnects) are no-ops.
                if (_rehydration is { } rehydrate)
                    _ = rehydrate.NotifyConnectedAsync();
                break;
        }
    }

    private DateTimeOffset? _stuckSince;

    private async Task AutoOpenOnSustainedDisconnectedAsync()
    {
        if (_stuckSince is not null) return; // a prior waiter is already armed
        _stuckSince = DateTimeOffset.UtcNow;
        var openedFor = _stuckSince.Value;

        await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        if (_stuckSince != openedFor) return;
        if (_connection?.Snapshot.Status is ConnectionStatus.Connected)
        {
            _stuckSince = null;
            return;
        }

        _mainWindow?.DispatcherQueue.TryEnqueue(ShowMainWindow);
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null) return;
        // Re-show through the AppWindow so a window previously hidden to the
        // tray (#102) comes back, not just a focus poke on a visible one.
        _mainWindow.ShowFromTray();
    }

    private void ShutdownCleanly()
    {
        // Let the main window's close-to-tray interception step aside for the
        // genuine exit, otherwise Exit() would just hide it (#102).
        _mainWindow?.AllowClose();
        ReportAgentKilledBeforeExit();
        Exit();
    }

    /// <summary>
    /// #110: if the student is quitting mid-session, tell the backend it was a
    /// deliberate departure (an <c>AgentKilled</c> event) so the teacher's roster
    /// updates immediately instead of waiting out the <c>HeartbeatLost</c>
    /// timeout. Time-boxed and best-effort: the bounded wait below is the only
    /// thing standing between a slow or failing network and the student's Quit, so
    /// a stalled report can never delay process exit. No-op outside a session
    /// (the coordinator gates on <c>JoinedSessionId</c>).
    /// </summary>
    private void ReportAgentKilledBeforeExit()
    {
        if (_coordinator is not { } coordinator) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            coordinator.ReportAgentKilledAsync(cts.Token).Wait(cts.Token);
        }
        catch
        {
            // Best-effort: a timeout (token fired) or any failure must not stop Quit.
        }
    }

    private static AgentConnectionState MapToTrayState(ConnectionStatus status) => status switch
    {
        ConnectionStatus.Connected => AgentConnectionState.Connected,
        ConnectionStatus.Connecting => AgentConnectionState.Connecting,
        ConnectionStatus.Reconnecting => AgentConnectionState.Reconnecting,
        ConnectionStatus.SigningIn => AgentConnectionState.Connecting,
        ConnectionStatus.Disconnected => AgentConnectionState.Disconnected,
        ConnectionStatus.SignInFailed => AgentConnectionState.SignedOut,
        _ => AgentConnectionState.SignedOut,
    };

    /// <summary>
    /// Dev-only path: skip host/WAM/hub bootstrap and just render the join
    /// toast against a synthetic payload, then exit after the countdown so
    /// scripts/dev/verify-toast.ps1 can screenshot it. See Program.cs.
    /// </summary>
    private void RunToastSelfTest()
    {
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        var logDir = AgentLogPaths.LocalAppDataLogDirectory();
        Directory.CreateDirectory(logDir);
        var serilog = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                path: Path.Combine(logDir, "focusagent-toasttest-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 3,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        _testLoggerFactory = LoggerFactory.Create(b => b.AddSerilog(serilog, dispose: true));
        var log = _testLoggerFactory.CreateLogger<App>();
        log.LogInformation("--show-test-toast: starting toast self-test");

        var ui = new WinUiSessionUiHost(dispatcher, _testLoggerFactory.CreateLogger<WinUiSessionUiHost>());
        var payload = new SessionStartedPayload(
            SessionId: Guid.Parse("00000000-0000-0000-0000-000000000041"),
            ClassId: Guid.NewGuid(),
            StartedAt: DateTimeOffset.UtcNow,
            JoinCode: "TOAST41",
            Apps: Array.Empty<AllowedAppDto>(),
            Domains: Array.Empty<AllowedDomainDto>());
        var confirmation = new JoinConfirmation(payload, "Self-Test Teacher", TimeSpan.FromSeconds(5), TimeProvider.System);

        // Kick off the show on the UI thread — ShowJoinConfirmationAsync
        // internally enqueues window creation. The returned Task completes when
        // the 5s countdown elapses; we then exit after a screenshot buffer.
        var showTask = ui.ShowJoinConfirmationAsync(confirmation);
        _ = showTask.ContinueWith(t =>
        {
            log.LogInformation(
                "--show-test-toast: confirmation completed status={Status} decision={Decision}",
                t.Status,
                t.Status == TaskStatus.RanToCompletion ? (object)t.Result : t.Exception?.Message ?? "<none>");
            return Task.Delay(TimeSpan.FromMilliseconds(1500))
                .ContinueWith(_ => dispatcher.TryEnqueue(Exit));
        });
    }

    /// <summary>
    /// Dev-only path: render the focus-enforcement overlay against a synthetic
    /// rules list with no host bootstrap, then exit after a buffer so
    /// scripts/dev/verify-overlay.ps1 can screenshot it. See Program.cs.
    /// </summary>
    private void RunOverlaySelfTest()
    {
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        var logDir = AgentLogPaths.LocalAppDataLogDirectory();
        Directory.CreateDirectory(logDir);
        var serilog = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                path: Path.Combine(logDir, "focusagent-overlaytest-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 3,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        _testLoggerFactory = LoggerFactory.Create(b => b.AddSerilog(serilog, dispose: true));
        var log = _testLoggerFactory.CreateLogger<App>();
        log.LogInformation("--show-test-overlay: starting overlay self-test");

        // WinUI's default DispatcherShutdownMode is OnLastWindowClose, which exits
        // the whole app the instant the overlay (our only window) is Close()d. That
        // made "Close() the overlay, then linger, then Exit()" impossible: the
        // process tore itself down ~½s after Close, turning the overlay's
        // torn-down-but-alive window into a sub-second transient that the #133
        // visual e2e raced — and lost under load (#160). Switch to explicit
        // shutdown so closing the overlay only destroys that window; this process
        // then stays up until we (or an observer that kills it) say so.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;

        var identifier = new AppIdentifier();
        var overlay = new WinUiFocusOverlay(
            dispatcher,
            identifier,
            _testLoggerFactory.CreateLogger<WinUiFocusOverlay>());

        var rules = new List<AllowedAppRule>
        {
            new() { MatchKind = AllowedAppMatchKind.ProcessName, Value = "winword" },
            new() { MatchKind = AllowedAppMatchKind.ProcessName, Value = "powerpnt" },
            new() { MatchKind = AllowedAppMatchKind.ExecutablePath, Value = @"C:\Program Files\GeoGebra\GeoGebra.exe" },
            new() { MatchKind = AllowedAppMatchKind.Publisher, Value = "International GeoGebra Institute" },
        };

        overlay.Show(rules, blockedAppName: "notepad");

        // Deterministic show -> close -> linger cycle so both consumers can observe
        // it without a real backend or off-list app:
        //   * scripts/dev/verify-overlay.ps1 finds the HWND and screenshots it
        //     partway through the initial ~5s hold;
        //   * the visual e2e (#133) captures it during that hold AND then asserts
        //     the close path actually tore the window down — its HWND goes invalid
        //     while the process is still alive, proving teardown rather than the
        //     window merely vanishing because the process died.
        // After the hold, Close() the overlay (which clears HWND_TOPMOST per the
        // #33 AC; the window's HWND goes invalid within tens of ms) and then keep
        // the process ALIVE for a long, fixed linger. The observer — not this
        // process — is the authority on teardown: it asserts it saw the HWND go
        // invalid while we were still running. The old code exited a fixed 3s
        // after Close, turning "torn down while alive" into a brief transient an
        // observer under load could miss (it reaches its teardown poll only after
        // a full-screen capture + PNG save + per-pixel analysis, which can overrun
        // by seconds) — that was the #160 flake. Lingering makes the torn-down-but-
        // alive state PERSIST until the observer samples it; both consumers kill
        // this process the moment they're done, so the linger only ever runs in
        // full as a safety net against a leaked process. A genuinely broken Close()
        // (window survives until process exit) still fails the e2e correctly: the
        // HWND stays valid for its whole poll, so it never observes teardown.
        _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ =>
        {
            dispatcher.TryEnqueue(overlay.Close);
            _ = Task.Delay(OverlayLingerAfterClose).ContinueWith(__ =>
                dispatcher.TryEnqueue(Exit));
        });
    }

    // How long the --show-test-overlay process stays alive after Close()ing the
    // overlay, so the torn-down-but-alive window state persists far longer than any
    // observer's teardown-poll budget (the #133 e2e polls ~12s) plus its capture
    // jitter. Both the e2e and verify-overlay.ps1 kill the process as soon as
    // they've captured/observed what they need, so this full linger only elapses as
    // a safety net against a leaked process. See RunOverlaySelfTest (#160).
    private static readonly TimeSpan OverlayLingerAfterClose = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Dev-only path (#173): show the redesigned <see cref="MainWindow"/> against
    /// a synthetic "connected, in a focus session" state with no host / WAM / hub
    /// / coordinator bootstrap, then keep the process alive so the visual e2e
    /// (MainWindowVisualTests) and scripts/dev can screenshot the real ink surface.
    /// The window's rendering path is production code — only the source of its
    /// state is synthetic (see MainWindow.CreateSelfTest). See Program.cs.
    /// </summary>
    private void RunMainWindowSelfTest()
    {
        // Like the overlay self-test: switch to explicit shutdown so the process
        // stays up after the window is shown (it isn't torn down here), and the
        // observer — the e2e or a verify script — kills it once it has captured.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;

        _mainWindow = MainWindow.CreateSelfTest();
        _mainWindow.Activate();
    }

    // Held by the --show-test-joinbycode path so the window outlives OnLaunched.
    private JoinByCodeWindow? _joinByCodeSelfTestWindow;

    /// <summary>
    /// Dev-only path (#175): show the real <see cref="JoinByCodeWindow"/> against a
    /// no-op join client with no host / WAM / hub bootstrap, then keep the process
    /// alive so the visual e2e (JoinByCodeVisualTests) and scripts/dev can
    /// screenshot the real ink surface (Space Mono code field, the magenta JOIN
    /// spark). The window's rendering path is production code — only its join
    /// client is a synthetic no-op. See Program.cs.
    /// </summary>
    private void RunJoinByCodeSelfTest()
    {
        // Like the other window self-tests: switch to explicit shutdown so the
        // process stays up after the window is shown (it isn't torn down here),
        // and the observer kills it once it has captured.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;

        _joinByCodeSelfTestWindow = new JoinByCodeWindow(new SelfTestJoinByCodeClient());
        Sessions.DialogWindowPositioner.ConfigureAndShow(_joinByCodeSelfTestWindow);
        // Render the "ready to join" state (a filled code, JOIN at full magenta)
        // so the visual e2e captures the spark, not the dimmed disabled button.
        // After ConfigureAndShow so the XAML island is realised and the
        // TextChanged → JOIN-enable path runs against a live control tree.
        _joinByCodeSelfTestWindow.PrefillForSelfTest();
    }

    /// <summary>
    /// A join client that never returns — the self-test renders the dialog's
    /// resting state (the redesigned ink surface) and is never driven through an
    /// actual join, so the call can simply hang until the process is killed.
    /// </summary>
    private sealed class SelfTestJoinByCodeClient : IJoinByCodeClient
    {
        public Task<JoinByCodeOutcome> JoinAsync(string code, CancellationToken ct = default) =>
            new TaskCompletionSource<JoinByCodeOutcome>().Task;
    }

    // Held by the --show-test-traymenu path so the host window outlives OnLaunched.
    private Window? _trayMenuSelfTestWindow;

    /// <summary>
    /// Dev-only path (#176): render the real tray context menu (the AA4 brand-styled
    /// flyout the <see cref="Tray.TrayIconHost"/> ships) so the visual e2e
    /// (TrayMenuVisualTests) and scripts/dev can screenshot its ink surface, Space
    /// Mono status row, on-ink actions and the one magenta spark.
    ///
    /// A tray <c>MenuFlyout</c> is a popup, not a window, and a headless run can't
    /// click the tray to open it — so this self-test builds the very same menu via
    /// the shared <see cref="Tray.TrayMenu.Build"/> factory and shows it open over a
    /// small ink host window. The menu's composition (DS brushes, fonts, the spark)
    /// is the production path; only the trigger (ShowAt instead of a tray click) is
    /// synthetic. The status row is driven to its "connected" state so the capture
    /// shows the live menu, not the resting "signed out" line. See Program.cs.
    /// </summary>
    private void RunTrayMenuSelfTest()
    {
        // Like the other self-tests: explicit shutdown so the process stays up after
        // the menu is shown; the observer kills it once it has captured.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;

        var menu = Tray.TrayMenu.Build(onOpen: () => { }, onJoinByCode: () => { }, onQuit: () => { });
        // Show a representative live state so the status eyebrow renders real text.
        menu.StatusItem.Text = "CONNECTED — SELF-TEST";

        // A small ink host window the flyout anchors to; sized so the open menu sits
        // wholly within (and over) it, which is the rect the e2e captures. The
        // window backs itself with the DS ink brush so any sliver around the menu is
        // still the brand surface, not the desktop.
        var root = new Microsoft.UI.Xaml.Controls.Grid
        {
            Background = (Microsoft.UI.Xaml.Media.Brush)Resources["PlinkSurfaceInkBrush"],
        };
        var host = new Window { Title = "Anchor — Tray menu (self-test)" };
        host.Content = root;
        _trayMenuSelfTestWindow = host;

        Sessions.DialogWindowPositioner.ConfigureAndShow(host);

        // Open the flyout once the XAML island is realised, anchored to the host
        // root so the popup overlays the captured rect. Re-show on a short cadence
        // so a late capture (under CI load) still finds the menu open rather than a
        // popup that auto-dismissed.
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        void ShowMenu()
        {
            if (root.XamlRoot is null) return;
            menu.Flyout.ShowAt(root, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
            {
                Position = new Windows.Foundation.Point(12, 12),
                ShowMode = Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowMode.Standard,
            });
        }
        var timer = dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(400);
        timer.Tick += (_, _) => ShowMenu();
        timer.Start();
        dispatcher.TryEnqueue(ShowMenu);
    }

    // Held by the --show-test-guided-install path so the window outlives OnLaunched.
    private Extension.GuidedInstallWindow? _guidedInstallSelfTestWindow;

    /// <summary>
    /// Dev-only path (#211): show the real <see cref="Extension.GuidedInstallWindow"/>
    /// — the guided-install fallback — against a no-op store launcher, with no WAM /
    /// hub / coordinator bootstrap, and keep the process alive so the visual e2e
    /// (GuidedInstallVisualTests) and scripts/dev can screenshot its ink surface.
    /// The window's rendering path is production code; only the store-launch side
    /// effect is a synthetic no-op (so the self-test never spawns Edge). This
    /// fallback is the primary path on a policy-locked box where the HKCU force-
    /// install write is denied, so it's a load-bearing surface worth observing.
    /// </summary>
    private void RunGuidedInstallSelfTest()
    {
        // Like the other window self-tests: explicit shutdown so the process stays
        // up after the window is shown; the observer kills it once it has captured.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;

        _guidedInstallSelfTestWindow = new Extension.GuidedInstallWindow(new NoOpStoreLauncher());
        Sessions.DialogWindowPositioner.ConfigureAndShow(_guidedInstallSelfTestWindow);
    }

    /// <summary>A store launcher that does nothing — the self-test renders the
    /// guided window's resting ink surface and never actually opens Edge.</summary>
    private sealed class NoOpStoreLauncher : IStoreLauncher
    {
        public void OpenStoreListing(string storeUrl) { }
    }

    /// <summary>
    /// Dev-only path (#164): assert the design-system WinUI binding actually
    /// resolved in the agent's own runtime — the merged dictionary's brushes are
    /// present (incl. the ink/on-ink family the agent leans on), a bundled font
    /// resource resolves, and Anchor's per-product accent override won over the
    /// binding's neutral default. This proves the *agent-side wiring* (App.xaml
    /// merge + accent override); the binding's own <c>--smoke</c> already proves
    /// the fonts physically load. Writes ds-theme-result.txt, sets the exit code,
    /// and exits. See Program.cs and scripts/dev/verify-ds-theme.ps1.
    /// </summary>
    private void RunDsThemeVerification()
    {
        var report = new System.Text.StringBuilder();
        var failures = new List<string>();

        report.AppendLine("Anchor agent — design-system binding wiring check (#164)");
        report.AppendLine();

        var res = Resources;
        object? Lookup(string key) => res.TryGetValue(key, out var v) ? v : null;
        static string Hex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

        void Brush(string key, Color expected, string why)
        {
            if (Lookup(key) is SolidColorBrush b && b.Color == expected)
            {
                report.AppendLine($"  ok   {key} = {Hex(b.Color)}  ({why})");
            }
            else
            {
                var actual = Lookup(key) is SolidColorBrush bad ? Hex(bad.Color) : "<missing/!brush>";
                var msg = $"{key} expected {Hex(expected)} but was {actual}  ({why})";
                report.AppendLine($"  FAIL {msg}");
                failures.Add(msg);
            }
        }

        // The merged PlinkResources.xaml is present: a foundation brush and the
        // ink family the student-facing agent composes on both resolve.
        Brush("PlinkMagentaBrush", Color.FromArgb(0xFF, 0xDB, 0x27, 0x77),
            "the magenta spark — proves the DS dictionary merged");
        Brush("PlinkOnInkBrush", Color.FromArgb(0xFF, 0xFA, 0xF7, 0xF2),
            "on-ink text — the ink treatment the agent uses");

        // The Anchor per-product accent override won over the binding default
        // (#FF1B1B23). This is the agent-specific wiring App.xaml adds.
        Brush("PlinkProductAccentBrush", Color.FromArgb(0xFF, 0x7E, 0x80, 0xD2),
            "Anchor's indigo on ink — the override beat the binding's neutral default");

        if (Lookup("PlinkMonoFontFamily") is FontFamily mono &&
            mono.Source.Contains("space-mono", StringComparison.OrdinalIgnoreCase))
        {
            report.AppendLine($"  ok   PlinkMonoFontFamily -> {mono.Source}  (bundled font resource resolved)");
        }
        else
        {
            var actual = Lookup("PlinkMonoFontFamily") is FontFamily f ? f.Source : "<missing/!fontfamily>";
            var msg = $"PlinkMonoFontFamily did not resolve to the bundled font (was '{actual}')";
            report.AppendLine($"  FAIL {msg}");
            failures.Add(msg);
        }

        // AF4 (#165): the agent is pinned to the DS ink treatment for every
        // student-facing window, NEVER following the OS light/dark setting
        // (ANCHOR_BRAND.md §6). RequestedTheme="Dark" in App.xaml is what stops
        // the system {ThemeResource} brushes from flipping with the OS; assert
        // it here so dropping the pin (and re-letting the agent follow the OS)
        // fails this self-test rather than silently shipping a light agent.
        if (RequestedTheme == ApplicationTheme.Dark)
        {
            report.AppendLine("  ok   RequestedTheme = Dark  (agent pinned to ink, never follows the OS)");
        }
        else
        {
            var msg = $"RequestedTheme expected Dark but was {RequestedTheme}  (agent must stay ink, not follow the OS — AF4)";
            report.AppendLine($"  FAIL {msg}");
            failures.Add(msg);
        }

        var ok = failures.Count == 0;
        report.AppendLine();
        report.AppendLine(ok ? "RESULT: PASS" : $"RESULT: FAIL ({failures.Count} failure(s))");

        try
        {
            File.WriteAllText(
                Path.Combine(AppContext.BaseDirectory, "ds-theme-result.txt"),
                report.ToString());
        }
        catch
        {
            // The exit code below is the authoritative signal; a write failure
            // (e.g. read-only dir) must not mask the real pass/fail.
        }

        Environment.ExitCode = ok ? 0 : 1;
        Exit();
    }

    private static void WriteStartupFailure(Exception ex)
    {
        try
        {
            var dir = AgentLogPaths.LocalAppDataLogDirectory();
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "startup-error.log"),
                $"{DateTimeOffset.Now:O}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // last-resort logger; intentionally swallow
        }
    }

    /// <summary>
    /// The hosting environment name that selects the per-deployment config layer
    /// (#203). An explicit <c>DOTNET_ENVIRONMENT</c> / <c>ASPNETCORE_ENVIRONMENT</c>
    /// always wins (the release pipeline can force either, and a dev can opt into
    /// Production locally to smoke-test the substituted file). Absent that, the
    /// default follows the build configuration: a Debug build — <c>dotnet run</c>
    /// and the headless e2e, which set no environment — defaults to
    /// <c>Development</c> so it keeps loading the dev overrides + dev backend; a
    /// Release build defaults to <c>Production</c> so the published agent picks up
    /// the substituted appsettings.Production.json. This is the inverse of the
    /// generic host's own default (always Production), which would silently skip the
    /// dev overrides for every local run.
    /// </summary>
    internal static string ResolveEnvironmentName()
    {
        var explicitName =
            Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        if (!string.IsNullOrWhiteSpace(explicitName))
            return explicitName;

#if DEBUG
        return "Development";
#else
        return "Production";
#endif
    }

    private static IHost BuildHost(DispatcherQueue dispatcher, Func<IntPtr> windowHandleProvider)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            // Select the per-deployment config layer (#203): a Debug build (dotnet
            // run / the headless e2e) defaults to Development and keeps loading
            // appsettings.Development.json; a Release build defaults to Production and
            // loads the substituted appsettings.Production.json. An explicit
            // DOTNET_ENVIRONMENT always wins. See ResolveEnvironmentName — set here
            // because the generic host's own default (always Production) would
            // silently skip the dev overrides for every local run.
            EnvironmentName = ResolveEnvironmentName(),
        });

        // Layer config: committed dev defaults (appsettings.json) first, then the
        // per-environment file. In Development that's the gitignored local override
        // (appsettings.Development.json); in a release build it's the committed
        // appsettings.Production.json *template*, whose Backend:BaseUrl + Auth
        // placeholders the release pipeline substitutes at pack time so a fork's
        // published agent targets its own backend + Entra without editing the
        // committed dev source (#203).
        builder.Configuration
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddJsonFile(
                $"appsettings.{builder.Environment.EnvironmentName}.json",
                optional: true,
                reloadOnChange: true);

        // --inject-token is the dev/headless gate (production never passes it).
        // Under it, layer environment variables LAST so they win over the JSON
        // above and a headless harness can override any setting per launch —
        // e.g. Backend__BaseUrl to point at a throwaway test backend on its own
        // port, Dev__ImpersonateOid to pick which seeded student to play, or
        // Session__HeartbeatIntervalSeconds to speed up the heartbeat e2e. This
        // is the agent-side analog of how the extension e2e harness overrides
        // the backend's config via env vars (extension/e2e/run-backend.ts). It
        // stays gated so production keeps its config strictly from the signed
        // appsettings files — a student can't repoint the agent via an env var.
        if (Program.InjectToken)
        {
            builder.Configuration.AddEnvironmentVariables();
        }

        builder.Services.AddOptions<BackendSettings>()
            .Bind(builder.Configuration.GetSection(BackendSettings.SectionName));
        builder.Services.AddOptions<AuthSettings>()
            .Bind(builder.Configuration.GetSection(AuthSettings.SectionName));
        builder.Services.AddOptions<RealtimeSettings>()
            .Bind(builder.Configuration.GetSection(RealtimeSettings.SectionName));
        builder.Services.AddOptions<SessionSettings>()
            .Bind(builder.Configuration.GetSection(SessionSettings.SectionName));
        builder.Services.AddOptions<DevSettings>()
            .Bind(builder.Configuration.GetSection(DevSettings.SectionName));

        builder.Services.AddSingleton(dispatcher);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<Func<IntPtr>>(_ => windowHandleProvider);
        // Capture the UI thread's SynchronizationContext so the foreground
        // watcher can marshal native callbacks onto it.
        builder.Services.AddSingleton(SynchronizationContext.Current
            ?? new DispatcherQueueSynchronizationContext(dispatcher));

        // --inject-token (dev only) swaps WAM for a no-op provider so the
        // agent can run headlessly and authenticate to the backend solely via
        // the X-Dev-Impersonate-Oid header (#44). Without the flag, the real
        // WAM provider runs as usual.
        if (Program.InjectToken)
        {
            builder.Services.AddSingleton<IAuthTokenProvider, InjectedTokenProvider>();
        }
        else
        {
            builder.Services.AddSingleton<IAuthTokenProvider, WamTokenProvider>();
        }
        builder.Services.AddSingleton<ISessionHubConnection, SignalRSessionHubConnection>();
        // --auto-join (dev only) replaces the WinUI toast with a host that
        // confirms immediately, so a headless run joins the session and can
        // receive mid-session bundle pushes (#93). Production always shows the
        // real toast.
        if (Program.AutoJoin)
            builder.Services.AddSingleton<ISessionUiHost, AutoConfirmSessionUiHost>();
        else
            builder.Services.AddSingleton<ISessionUiHost, WinUiSessionUiHost>();
        builder.Services.AddSingleton<SessionCoordinator>();
        builder.Services.AddSingleton<SessionHeartbeatService>();
        // #54 -- post-restart session rehydration: REST client + the service
        // that fans backend results into SessionCoordinator.RejoinAsync.
        builder.Services.AddHttpClient<ISessionRehydrationClient, HttpSessionRehydrationClient>();
        builder.Services.AddSingleton<SessionRehydrationService>();
        // #34 -- manual join-by-code: REST client + dialog flow host.
        builder.Services.AddHttpClient<IJoinByCodeClient, HttpJoinByCodeClient>();
        builder.Services.AddSingleton<JoinByCodeFlow>();
        builder.Services.AddSingleton<ConnectionManager>();

        builder.Services.AddSingleton<IAppIdentifier, AppIdentifier>();
        builder.Services.AddSingleton<IForegroundWatcher, ForegroundWatcher>();
        builder.Services.AddSingleton<IWindowEnumerator, WindowEnumerator>();
        builder.Services.AddSingleton<IFocusEnforcer, FocusEnforcer>();
        builder.Services.AddSingleton<IFocusEventReporter, SignalRFocusEventReporter>();
        builder.Services.AddSingleton<IFocusOverlay, WinUiFocusOverlay>();
        builder.Services.AddSingleton<FocusSessionController>();

        // #146 part 1 -- agent-as-witness tamper detection. The named-pipe
        // transport hosts the link the browser's native messaging host connects
        // to; the monitor turns a drop during a joined session into a
        // TamperDetected{extension_disabled} report. JoinedSessionId (not
        // ActiveSessionId) gates it: the hub only accepts events from a joined
        // participant, and a drop outside a session is just a closed browser.
        builder.Services.AddSingleton<ITamperReporter, SignalRTamperReporter>();
        builder.Services.AddSingleton<IExtensionWitnessTransport, NamedPipeWitnessTransport>();
        builder.Services.AddSingleton(sp => new ExtensionWitnessMonitor(
            sp.GetRequiredService<IExtensionWitnessTransport>(),
            sp.GetRequiredService<ITamperReporter>(),
            () => sp.GetRequiredService<SessionCoordinator>().JoinedSessionId,
            sp.GetRequiredService<ILogger<ExtensionWitnessMonitor>>()));

        // #148 -- agent-side robust InPrivate detection. The scanner enumerates
        // live Edge windows; the monitor polls it while in a joined session and
        // reports TamperDetected{inprivate_opened} for each newly-seen InPrivate
        // window. --simulate-inprivate (dev only) swaps a synthetic scanner so the
        // headless e2e can drive the path without a real InPrivate window.
        if (Program.SimulateInPrivate)
            builder.Services.AddSingleton<IBrowserWindowScanner, SimulatedInPrivateScanner>();
        else
            builder.Services.AddSingleton<IBrowserWindowScanner, BrowserWindowScanner>();
        builder.Services.AddSingleton<InPrivateWitnessMonitor>();

        // #211 -- self-register the Edge extension. On first run the agent writes
        // the per-user ExtensionInstallForcelist policy so Edge force-installs +
        // pins the canonical listing (#210); the witness monitor's connected state
        // is the success signal, and if the extension hasn't checked in within the
        // grace period the guided-install window opens the store. The store launch
        // and registry write are the real platform implementations; the registrar
        // itself is pure (unit-tested).
        builder.Services.AddSingleton<IExtensionPolicyStore>(
            sp => new RegistryExtensionPolicyStore(
                keyPathOverride: null,
                sp.GetRequiredService<ILogger<RegistryExtensionPolicyStore>>()));
        builder.Services.AddSingleton<IStoreLauncher>(
            sp => new EdgeStoreLauncher(sp.GetRequiredService<ILogger<EdgeStoreLauncher>>()));
        builder.Services.AddSingleton(sp => new ExtensionSelfRegistrar(
            sp.GetRequiredService<IExtensionPolicyStore>(),
            // Success signal: the extension has connected over the witness link.
            extensionCheckedIn: () => sp.GetRequiredService<ExtensionWitnessMonitor>().IsConnected,
            // Fallback: open the guided one-click install on the UI thread.
            showGuidedInstall: ct => sp.GetRequiredService<GuidedInstallLauncher>().ShowAsync(ct),
            log: sp.GetRequiredService<ILogger<ExtensionSelfRegistrar>>()));
        builder.Services.AddSingleton(sp => new GuidedInstallLauncher(
            sp.GetRequiredService<DispatcherQueue>(),
            sp.GetRequiredService<IStoreLauncher>()));

        var logDir = AgentLogPaths.LocalAppDataLogDirectory();
        Directory.CreateDirectory(logDir);

        var serilog = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                path: Path.Combine(logDir, "focusagent-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(serilog, dispose: true);
        builder.Logging.AddDebug();

        return builder.Build();
    }
}
