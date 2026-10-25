using System.Net;
using System.Reflection;
using System.Text.Json;
using FocusAgent.App.Connectivity;
using FocusAgent.Core.Focus;
using FocusAgent.Core.Sessions;
using FocusAgent.Core.Tamper;
using Microsoft.Extensions.Logging;

namespace FocusAgent.App.Connectivity;

/// <summary>
/// Dev-only loopback HTTP listener (no Kestrel — uses
/// <see cref="HttpListener"/> from BCL, zero deps) that exposes the agent's
/// current connection + session state as JSON. Lets the verify-session-start
/// script (and any other headless caller) poll the agent's actual state
/// instead of guessing from screenshots or grepping logs.
///
/// Started on demand when the agent is launched with
/// <c>--status-endpoint &lt;port&gt;</c>. Binds only to 127.0.0.1 so it's
/// unreachable from another machine even if the dev forgets to disable it.
///
/// Endpoint:
///   GET /status -> {
///       agentVersion,      // the agent's own version, from the single version source (#208)
///       connectionStatus, displayName, lastError,
///       activeSessionId,   // non-null while the toast is up or the user has joined
///       joinedSessionId,   // non-null after user confirmed
///       allowedApps,       // current matcher app rules, or null when not in a session (#93)
///       inPrivateDetections // count of Edge InPrivate windows the agent-side witness has reported (#148)
///   }
///
/// Poll <c>activeSessionId</c> to know whether SessionStarted reached the
/// agent UI — it flips to non-null the moment the coordinator handles the
/// hub event, before the 5s countdown elapses.
///
/// It also exposes two POST controls so the headless e2e can drive the #102 UI
/// actions without UI automation (both no-op if the agent wasn't given the
/// corresponding callback):
///   POST /leave -> the student "Leave session" button: emits ManualLeave and
///                  ends the session locally while the agent keeps running.
///   POST /close -> the window "Close" button: hides the window to the tray.
///   POST /quit  -> the tray's "Exit Anchor" action (#110): shuts the agent down
///                  cleanly. Under the tray's rule (#445): while a session is
///                  going on the tray offers no Exit, so this answers 409 and the
///                  agent keeps running.
///   POST /extension-consent?answer=allow|not-now
///               -> the Edge extension setup window's buttons (#405): answers
///                  the open window like a click (404 when none is open). In a
///                  dev/test run "allow" never leads to a real UAC prompt.
///   POST /extension-setup
///               -> the tray's "Set up the Edge extension…" item (#405): opens
///                  the setup window again, even after a "not now".
///   POST /extension-repair
///               -> the paused-Edge overlay's "switch the extension back on"
///                  action (#427): opens Edge for the repair window (409 when
///                  Edge isn't paused).
///   POST /overlay-app?app=&lt;rule value&gt;
///               -> the focus overlay's row for that allowed app (#453): clicks it
///                  on the UI thread, as the student does, without waiting for
///                  the click (409 when the overlay isn't up).
///
/// And /status carries <c>extensionPolicy</c> (#405): where the Anchor
/// force-install entry was found (<c>None</c> / <c>Machine</c> / <c>User</c>)
/// and whether the setup window is open; <c>extensionGuard</c> (#427):
/// whether the extension's witness is connected and the Edge pause's state; and
/// <c>overlay</c> (#452): what the focus overlay shows while it is up
/// (<c>Blocked</c> / <c>BrowserPaused</c>), null while it is not; and
/// <c>pendingUpdate</c> (#449): the version of a downloaded update that the next
/// agent start applies, null while none is waiting; and <c>lastUpdateCheck</c>
/// (#456): when the last update check started, by the agent's clock.
///
/// Two more POST controls drive the #456 update restart:
///   POST /check-update
///               -> run an update check now, as the interval timer does.
///   POST /simulate-sleep?slept=&lt;hh:mm:ss&gt;
///               -> what time asleep does to the agent: the update clock jumps
///                  by <c>slept</c> while the timers stay put. The e2e then sends
///                  the wake itself, the way Windows does (a power or session
///                  message to the agent's SystemEvents window).
/// </summary>
public sealed class StatusEndpoint : IAsyncDisposable
{
    /// <summary>
    /// The agent's own version, as baked into the assembly from the single
    /// version source (agent/Directory.Build.props, #208). Read from the
    /// InformationalVersion that MSBuild derives from &lt;Version&gt;; the
    /// "+&lt;commit&gt;" SourceLink suffix (if any) is trimmed so the reported
    /// value is the clean SemVer the build was stamped with. This is the runtime
    /// proof that the single version source actually flows into the produced exe.
    /// </summary>
    public static string AgentVersion { get; } = ResolveAgentVersion();

    private static string ResolveAgentVersion()
    {
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (string.IsNullOrEmpty(informational))
            return "0.0.0";
        // Strip the SourceLink "+<sha>" build metadata if present.
        var plus = informational.IndexOf('+');
        return plus >= 0 ? informational[..plus] : informational;
    }

    private readonly ConnectionManager _connection;
    private readonly SessionCoordinator _coordinator;
    private readonly FocusSessionController _focus;
    private readonly InPrivateWitnessMonitor _inPrivate;
    private readonly ILogger<StatusEndpoint> _log;
    private readonly Func<CancellationToken, Task>? _onLeaveSession;
    private readonly Action? _onCloseWindow;
    private readonly Action? _onQuit;
    private readonly Func<bool>? _canQuit;
    private readonly Func<object?>? _extensionPolicy;
    private readonly Func<bool, bool>? _onAnswerExtensionConsent;
    private readonly Action? _onOfferExtensionSetup;
    private readonly Func<object?>? _extensionGuard;
    private readonly Func<bool>? _onTurnExtensionBackOn;
    private readonly Func<string?>? _overlay;
    private readonly Func<string, bool>? _onClickOverlayApp;
    private readonly Func<string?>? _pendingUpdate;
    private readonly Func<DateTimeOffset?>? _lastUpdateCheck;
    private readonly Action? _onCheckUpdate;
    private readonly Action<TimeSpan>? _onSimulateSleep;
    private readonly HttpListener _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public StatusEndpoint(
        ConnectionManager connection,
        SessionCoordinator coordinator,
        FocusSessionController focus,
        InPrivateWitnessMonitor inPrivate,
        ILogger<StatusEndpoint> log,
        Func<CancellationToken, Task>? onLeaveSession = null,
        Action? onCloseWindow = null,
        Action? onQuit = null,
        Func<object?>? extensionPolicy = null,
        Func<bool, bool>? onAnswerExtensionConsent = null,
        Action? onOfferExtensionSetup = null,
        Func<object?>? extensionGuard = null,
        Func<bool>? onTurnExtensionBackOn = null,
        Func<string?>? overlay = null,
        Func<string, bool>? onClickOverlayApp = null,
        Func<string?>? pendingUpdate = null,
        Func<DateTimeOffset?>? lastUpdateCheck = null,
        Action? onCheckUpdate = null,
        Action<TimeSpan>? onSimulateSleep = null,
        Func<bool>? canQuit = null)
    {
        _connection = connection;
        _coordinator = coordinator;
        _focus = focus;
        _inPrivate = inPrivate;
        _log = log;
        _onLeaveSession = onLeaveSession;
        _onCloseWindow = onCloseWindow;
        _onQuit = onQuit;
        _extensionPolicy = extensionPolicy;
        _onAnswerExtensionConsent = onAnswerExtensionConsent;
        _onOfferExtensionSetup = onOfferExtensionSetup;
        _extensionGuard = extensionGuard;
        _onTurnExtensionBackOn = onTurnExtensionBackOn;
        _overlay = overlay;
        _onClickOverlayApp = onClickOverlayApp;
        _pendingUpdate = pendingUpdate;
        _lastUpdateCheck = lastUpdateCheck;
        _onCheckUpdate = onCheckUpdate;
        _onSimulateSleep = onSimulateSleep;
        _canQuit = canQuit;
        _listener = new HttpListener();
    }

    public void Start(int port)
    {
        var prefix = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(prefix);
        _listener.Start();
        _log.LogWarning(
            "Status endpoint listening on {Prefix} (DEV ONLY). Never enable on a machine reachable from the network.",
            prefix);

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException) { return; }
            catch (HttpListenerException) { return; }

            _ = Task.Run(() => HandleAsync(ctx), ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath;
            var method = ctx.Request.HttpMethod;

            if (method == "POST" && path == "/leave")
            {
                if (_onLeaveSession is { } leave)
                    await leave(CancellationToken.None).ConfigureAwait(false);
                ctx.Response.StatusCode = _onLeaveSession is null ? 404 : 204;
                ctx.Response.Close();
                return;
            }

            if (method == "POST" && path == "/close")
            {
                _onCloseWindow?.Invoke();
                ctx.Response.StatusCode = _onCloseWindow is null ? 404 : 204;
                ctx.Response.Close();
                return;
            }

            if (method == "POST" && path == "/quit")
            {
                // Respond *before* triggering quit: ShutdownCleanly tears the
                // process down, so the 204 must already be on the wire. The
                // callback marshals onto the UI thread (see App.OnLaunched).
                // #445: 409 while a session is going on, when the tray offers no
                // Exit, so a spec can tell "quitting" from "refused".
                var status = _onQuit is null ? 404 : _canQuit?.Invoke() == false ? 409 : 204;
                ctx.Response.StatusCode = status;
                ctx.Response.Close();
                if (status == 204) _onQuit!.Invoke();
                else if (status == 409)
                    _log.LogInformation("Quit refused: a session is going on, and the tray offers no Exit then.");
                return;
            }

            if (method == "POST" && path == "/extension-consent")
            {
                var answer = ctx.Request.QueryString["answer"];
                var allow = string.Equals(answer, "allow", StringComparison.OrdinalIgnoreCase);
                var valid = allow || string.Equals(answer, "not-now", StringComparison.OrdinalIgnoreCase);
                var answered = valid && _onAnswerExtensionConsent?.Invoke(allow) == true;
                ctx.Response.StatusCode = !valid ? 400 : answered ? 204 : 404;
                ctx.Response.Close();
                return;
            }

            if (method == "POST" && path == "/extension-setup")
            {
                _onOfferExtensionSetup?.Invoke();
                ctx.Response.StatusCode = _onOfferExtensionSetup is null ? 404 : 204;
                ctx.Response.Close();
                return;
            }

            if (method == "POST" && path == "/extension-repair")
            {
                // 409 when Edge isn't paused, so a spec can tell "opened" from "no-op".
                var opened = _onTurnExtensionBackOn?.Invoke();
                ctx.Response.StatusCode = opened switch { null => 404, true => 204, false => 409 };
                ctx.Response.Close();
                return;
            }

            if (method == "POST" && path == "/overlay-app")
            {
                // 409 when the overlay isn't up, so a spec can tell "clicked" from "no-op".
                var app = ctx.Request.QueryString["app"];
                var clicked = string.IsNullOrWhiteSpace(app) ? null : _onClickOverlayApp?.Invoke(app);
                ctx.Response.StatusCode = string.IsNullOrWhiteSpace(app)
                    ? 400
                    : clicked switch { null => 404, true => 204, false => 409 };
                ctx.Response.Close();
                return;
            }

            if (method == "POST" && path == "/check-update")
            {
                _onCheckUpdate?.Invoke();
                ctx.Response.StatusCode = _onCheckUpdate is null ? 404 : 204;
                ctx.Response.Close();
                return;
            }

            if (method == "POST" && path == "/simulate-sleep")
            {
                var valid = TimeSpan.TryParse(
                                ctx.Request.QueryString["slept"],
                                System.Globalization.CultureInfo.InvariantCulture,
                                out var slept)
                            && slept >= TimeSpan.Zero;
                if (valid) _onSimulateSleep?.Invoke(slept);
                ctx.Response.StatusCode = !valid ? 400 : _onSimulateSleep is null ? 404 : 204;
                ctx.Response.Close();
                return;
            }

            if (path != "/status")
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }

            var snap = _connection.Snapshot;
            var sweep = _focus.GetLastStartupSweep();
            var payload = new
            {
                // #208: the agent's own version, proving the single version
                // source (agent/Directory.Build.props) flowed into this exe.
                agentVersion = AgentVersion,
                connectionStatus = snap.Status.ToString(),
                displayName = snap.DisplayName,
                lastError = snap.LastError,
                activeSessionId = _coordinator.ActiveSessionId,
                joinedSessionId = _coordinator.JoinedSessionId,
                allowedApps = _focus.GetActiveAllowedApps(),
                // #104: the session-start sweep result, so the headless e2e can
                // assert the off-list windows were minimized. Null until the
                // first session of this process has started.
                startupSweep = sweep is null
                    ? null
                    : (object)new
                    {
                        windowsExamined = sweep.WindowsExamined,
                        minimizedProcesses = sweep.MinimizedProcesses,
                    },
                // #148: how many Edge InPrivate windows the agent-side witness has
                // reported this process, so the headless e2e can assert detection
                // fired without screenshotting a real InPrivate window.
                inPrivateDetections = _inPrivate.DetectionCount,
                // #405: where the Edge force-install entry is, and whether the
                // setup window is up, so the headless e2e can follow the flow.
                extensionPolicy = _extensionPolicy?.Invoke(),
                // #427: whether the extension's witness is connected and where the
                // Edge pause stands, so the headless e2e can follow a switch-off.
                extensionGuard = _extensionGuard?.Invoke(),
                // #452: which overlay the student is on, if any, so the headless
                // e2e can tell the paused-browser overlay from the blocked-app one.
                overlay = _overlay?.Invoke(),
                // #449: the downloaded update the next agent start applies, so the
                // headless e2e can tell the update pass finished while the agent
                // kept running.
                pendingUpdate = _pendingUpdate?.Invoke(),
                // #456: when the last update check started, so the headless e2e can
                // tell whether a wake ran one.
                lastUpdateCheck = _lastUpdateCheck?.Invoke(),
            };
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false,
            });

            ctx.Response.ContentType = "application/json";
            ctx.Response.StatusCode = 200;
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            ctx.Response.Close();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Status endpoint handler faulted");
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { }
        }
        _cts?.Dispose();
    }
}
