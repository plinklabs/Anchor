using FocusAgent.Core.Auth;
using FocusAgent.Core.Realtime;
using FocusAgent.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;

namespace FocusAgent.App.Connectivity;

/// <summary>
/// Owns the agent's connect-and-stay-connected lifecycle: acquires the WAM
/// token, starts the SignalR hub, and surfaces a single observable state to
/// the rest of the UI. Replaces the inline orchestration that used to live in
/// <c>App.StartHubAsync</c>, which had two terminal failure modes that the
/// user could only escape by relaunching the agent:
///
///  * a one-shot token-acquire that just returned on error (e.g. user cancels
///    the WAM picker) — left the tray stuck Disconnected with no retry path;
///  * no recovery after the hub fires <c>Closed</c> (server abort, exhausted
///    reconnect policy, …) — also left the tray stuck Disconnected.
///
/// <see cref="StartAsync"/> kicks off the initial flow; <see cref="RetryAsync"/>
/// is the user-triggered recovery (the "Sign in / Reconnect" button on
/// <c>MainWindow</c>). A semaphore makes the two safe to call concurrently;
/// the <see cref="Status"/> stream is the single source of truth for UI.
/// </summary>
public sealed class ConnectionManager : IAsyncDisposable
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly IAuthTokenProvider _tokens;
    private readonly ISessionHubConnection _hub;
    private readonly ILogger<ConnectionManager> _log;
    private readonly string _backendBaseUrl;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ConnectionStatus _statusKind = ConnectionStatus.Idle;
    private string? _displayName;
    private string? _lastError;
    private bool _disposed;

    public ConnectionManager(
        IAuthTokenProvider tokens,
        ISessionHubConnection hub,
        IOptions<BackendSettings> backend,
        ILogger<ConnectionManager> log)
    {
        _tokens = tokens;
        _hub = hub;
        _backendBaseUrl = backend.Value.BaseUrl;
        _log = log;

        // Mirror SignalR's protocol-level state into ours so a transient drop
        // becomes Reconnecting in the UI instead of "still Connected".
        _hub.StateChanged += OnHubStateChanged;
    }

    public event EventHandler<ConnectionStatusSnapshot>? StatusChanged;

    public ConnectionStatusSnapshot Snapshot => new(_statusKind, _displayName, _lastError);

    /// <summary>
    /// Initial entry from <c>App.OnLaunched</c>. Fire-and-forget by the caller;
    /// the manager will retry the hub forever and surface failures via
    /// <see cref="StatusChanged"/>. Token-acquire failures are surfaced as
    /// <see cref="ConnectionStatus.SignInFailed"/> and wait for <see cref="RetryAsync"/>.
    /// </summary>
    public Task StartAsync() => RunAsync(userInitiated: false);

    /// <summary>
    /// User-triggered retry. Safe to call from any state — the gate serialises
    /// against an in-flight automatic attempt, and the run is skipped (with a
    /// log line) if we're already Connected.
    /// </summary>
    public Task RetryAsync() => RunAsync(userInitiated: true);

    private async Task RunAsync(bool userInitiated)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;

            if (_statusKind == ConnectionStatus.Connected && !userInitiated)
            {
                _log.LogDebug("ConnectionManager.RunAsync skipped — already Connected");
                return;
            }

            // Step 1: token. A single attempt; if it fails (broker, user
            // cancel, network), surface SignInFailed and stop. Recovery is
            // RetryAsync triggered by the user from MainWindow.
            SetStatus(ConnectionStatus.SigningIn);
            AuthResult auth;
            try
            {
                auth = await _tokens.AcquireTokenAsync().ConfigureAwait(false);
                _displayName = auth.DisplayName;
                _log.LogInformation("Sign-in succeeded for {Username}", auth.Username);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Sign-in failed");
                _lastError = DescribeAuthFailure(ex);
                SetStatus(ConnectionStatus.SignInFailed);
                return;
            }

            // Step 2: hub. Retry forever with exponential backoff up to 30s.
            // Surfaces Connecting on the first try and Reconnecting on later
            // tries so the UI shows progress.
            var attempt = 0;
            while (true)
            {
                if (_disposed) return;

                SetStatus(attempt == 0 ? ConnectionStatus.Connecting : ConnectionStatus.Reconnecting);
                try
                {
                    // Normalise hub state to Disconnected before starting.
                    // SignalR's StartAsync throws "cannot be started if it is
                    // not in the Disconnected state" if the hub is mid-Connect
                    // or mid-Reconnect (e.g. a user-triggered retry hits while
                    // auto-reconnect is already in flight). StopAsync is a
                    // no-op when already Disconnected.
                    try { await _hub.StopAsync().ConfigureAwait(false); } catch { }
                    await _hub.StartAsync().ConfigureAwait(false);
                    _lastError = null;
                    SetStatus(ConnectionStatus.Connected);
                    return;
                }
                catch (Exception ex)
                {
                    attempt++;
                    var delay = NextBackoff(attempt);
                    _log.LogWarning(ex,
                        "Hub connect failed (attempt {Attempt}); retrying in {Seconds:0}s",
                        attempt, delay.TotalSeconds);
                    _lastError = DescribeConnectFailure(ex);
                    SetStatus(ConnectionStatus.Disconnected);
                    await Task.Delay(delay).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OnHubStateChanged(object? _, AgentConnectionState protocolState)
    {
        // The hub's auto-reconnect handles transient drops on its own; we
        // only need to react when it gives up (Disconnected) AFTER having
        // been Connected, which means Closed fired. Restart the full flow.
        switch (protocolState)
        {
            case AgentConnectionState.Reconnecting:
                if (_statusKind == ConnectionStatus.Connected)
                    SetStatus(ConnectionStatus.Reconnecting);
                break;
            case AgentConnectionState.Disconnected:
                if (_statusKind is ConnectionStatus.Connected or ConnectionStatus.Reconnecting)
                {
                    // Only kick off a recovery if one isn't already running.
                    // Otherwise we'd queue waiters behind the gate forever as
                    // each retry-loop iteration ends up firing Disconnected.
                    if (_gate.CurrentCount > 0)
                    {
                        _log.LogWarning("Hub Closed after being Connected — kicking off reconnect cycle.");
                        SetStatus(ConnectionStatus.Disconnected);
                        _ = RunAsync(userInitiated: false);
                    }
                }
                break;
            case AgentConnectionState.Connected:
                // Protocol-level recovery (Reconnected event) — our status
                // already updates inside RunAsync for the initial connect.
                if (_statusKind == ConnectionStatus.Reconnecting)
                    SetStatus(ConnectionStatus.Connected);
                break;
        }
    }

    private void SetStatus(ConnectionStatus next)
    {
        _statusKind = next;
        var snapshot = Snapshot;
        _log.LogDebug("Connection status -> {Status}", next);
        StatusChanged?.Invoke(this, snapshot);
    }

    private static TimeSpan NextBackoff(int attempt)
    {
        var seconds = Math.Min(MaxBackoff.TotalSeconds, Math.Pow(2, Math.Min(attempt, 5)));
        return TimeSpan.FromSeconds(seconds);
    }

    private static string DescribeAuthFailure(Exception ex) => ex switch
    {
        MsalException msal when !string.IsNullOrWhiteSpace(msal.ErrorCode) =>
            $"Sign-in failed ({msal.ErrorCode}). Click Sign in to try again.",
        _ => "Sign-in failed. Click Sign in to try again.",
    };

    private string DescribeConnectFailure(Exception ex)
    {
        // HttpRequestException covers BOTH connection-refused/DNS errors AND
        // HTTP status responses (e.g. 401 from the hub negotiate). Distinguish
        // them — a 401 says the backend is reachable but rejected our auth,
        // which is a completely different bug class than network unreachable.
        if (ex is System.Net.Http.HttpRequestException hre)
        {
            if (hre.StatusCode is { } sc)
                return $"Backend rejected the connection with HTTP {(int)sc} ({sc}). Retrying…";
            return $"Can't reach the backend at {_backendBaseUrl}. Retrying…";
        }
        return $"Connection failed: {ex.Message}. Retrying…";
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _hub.StateChanged -= OnHubStateChanged;
        _gate.Dispose();
        await ValueTask.CompletedTask;
    }
}

public enum ConnectionStatus
{
    Idle,
    SigningIn,
    Connecting,
    Connected,
    Reconnecting,
    Disconnected,
    SignInFailed,
}

public readonly record struct ConnectionStatusSnapshot(
    ConnectionStatus Status,
    string? DisplayName,
    string? LastError);
