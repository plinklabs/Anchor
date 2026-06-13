using FocusAgent.App.Auth;
using FocusAgent.App.Connectivity;
using FocusAgent.App.Focus;
using FocusAgent.App.Realtime;
using FocusAgent.App.Sessions;
using FocusAgent.App.Tamper;
using FocusAgent.App.Tray;
using FocusAgent.Core.Auth;
using FocusAgent.Core.Dtos;
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
using Serilog;
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

    private static IHost BuildHost(DispatcherQueue dispatcher, Func<IntPtr> windowHandleProvider)
    {
        var builder = Host.CreateApplicationBuilder();

        builder.Configuration
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true);

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
