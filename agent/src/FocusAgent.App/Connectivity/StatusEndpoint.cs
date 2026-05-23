using System.Net;
using System.Text.Json;
using FocusAgent.App.Connectivity;
using FocusAgent.Core.Sessions;
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
///       joinedSessionId    // non-null after user confirmed
///   }
///
/// Poll <c>activeSessionId</c> to know whether SessionStarted reached the
/// agent UI — it flips to non-null the moment the coordinator handles the
/// hub event, before the 5s countdown elapses.
/// </summary>
public sealed class StatusEndpoint : IAsyncDisposable
{
    private readonly ConnectionManager _connection;
    private readonly SessionCoordinator _coordinator;
    private readonly ILogger<StatusEndpoint> _log;
    private readonly HttpListener _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public StatusEndpoint(
        ConnectionManager connection,
        SessionCoordinator coordinator,
        ILogger<StatusEndpoint> log)
    {
        _connection = connection;
        _coordinator = coordinator;
        _log = log;
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
            if (ctx.Request.Url?.AbsolutePath != "/status")
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }

            var snap = _connection.Snapshot;
            var payload = new
            {
                connectionStatus = snap.Status.ToString(),
                displayName = snap.DisplayName,
                lastError = snap.LastError,
                activeSessionId = _coordinator.ActiveSessionId,
                joinedSessionId = _coordinator.JoinedSessionId,
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
