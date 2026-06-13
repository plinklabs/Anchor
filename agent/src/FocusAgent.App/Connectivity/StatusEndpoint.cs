using System.Net;
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
///   POST /quit  -> the tray/window "Quit" action (#110): shuts the agent down
///                  cleanly, which emits a best-effort AgentKilled event first
///                  if quit happens mid-session.
/// </summary>
public sealed class StatusEndpoint : IAsyncDisposable
{
    private readonly ConnectionManager _connection;
    private readonly SessionCoordinator _coordinator;
    private readonly FocusSessionController _focus;
    private readonly InPrivateWitnessMonitor _inPrivate;
    private readonly ILogger<StatusEndpoint> _log;
    private readonly Func<CancellationToken, Task>? _onLeaveSession;
    private readonly Action? _onCloseWindow;
    private readonly Action? _onQuit;
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
        Action? onQuit = null)
    {
        _connection = connection;
        _coordinator = coordinator;
        _focus = focus;
        _inPrivate = inPrivate;
        _log = log;
        _onLeaveSession = onLeaveSession;
        _onCloseWindow = onCloseWindow;
        _onQuit = onQuit;
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
                var canQuit = _onQuit is not null;
                ctx.Response.StatusCode = canQuit ? 204 : 404;
                ctx.Response.Close();
                if (canQuit) _onQuit!.Invoke();
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
