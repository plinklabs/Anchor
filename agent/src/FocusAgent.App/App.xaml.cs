using FocusAgent.App.Auth;
using FocusAgent.App.Connectivity;
using FocusAgent.App.Focus;
using FocusAgent.App.Realtime;
using FocusAgent.App.Sessions;
using FocusAgent.App.Tray;
using FocusAgent.Core.Auth;
using FocusAgent.Core.Dtos;
using FocusAgent.Core.Focus;
using FocusAgent.Core.Logging;
using FocusAgent.Core.Realtime;
using FocusAgent.Core.Sessions;
using FocusAgent.Core.Settings;
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
            // Also resolve the rehydration service eagerly so it's ready when
            // the connection manager fires its first Connected event below.
            _rehydration = _host.Services.GetRequiredService<SessionRehydrationService>();
            _focus = _host.Services.GetRequiredService<FocusSessionController>();
            _connection = _host.Services.GetRequiredService<ConnectionManager>();

            _mainWindow = new MainWindow(
                _connection,
                _coordinator,
                _heartbeat,
                _host.Services.GetRequiredService<IOptions<SessionSettings>>(),
                onQuit: ShutdownCleanly);
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
                    _host.Services.GetRequiredService<ILogger<StatusEndpoint>>());
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
        _mainWindow.Activate();
    }

    private void ShutdownCleanly()
    {
        Exit();
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

        // Deterministic show -> close -> exit cycle so both consumers can observe
        // it without a real backend or off-list app:
        //   * scripts/dev/verify-overlay.ps1 finds the HWND and screenshots it
        //     partway through the initial ~5s hold;
        //   * the visual e2e (#133) captures it during that hold AND then asserts
        //     the close path actually tore the window down — by Close()-ing it a
        //     full 3s before the process exits, so its HWND goes invalid while
        //     the process is still alive, proving teardown rather than the window
        //     merely vanishing because the process died.
        // Close clears HWND_TOPMOST per the #33 AC.
        _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ =>
        {
            dispatcher.TryEnqueue(overlay.Close);
            _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(__ =>
                dispatcher.TryEnqueue(Exit));
        });
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
        builder.Services.AddSingleton<IFocusEnforcer, FocusEnforcer>();
        builder.Services.AddSingleton<IFocusEventReporter, SignalRFocusEventReporter>();
        builder.Services.AddSingleton<IFocusOverlay, WinUiFocusOverlay>();
        builder.Services.AddSingleton<FocusSessionController>();

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
