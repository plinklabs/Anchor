using FocusAgent.Core.Dtos;

namespace FocusAgent.Core.Realtime;

public interface ISessionHubConnection : IAsyncDisposable
{
    AgentConnectionState State { get; }

    event EventHandler<AgentConnectionState>? StateChanged;
    event EventHandler<SessionStartedPayload>? SessionStarted;
    event EventHandler<Guid>? SessionEnded;

    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);

    Task JoinSessionAsync(Guid sessionId, string? joinCode, CancellationToken ct = default);
    Task LeaveSessionAsync(Guid sessionId, CancellationToken ct = default);
    Task DeclineSessionAsync(Guid sessionId, string reason, CancellationToken ct = default);
    Task ReportEventAsync(Guid sessionId, string kind, string payloadJson, DateTimeOffset? occurredAt = null, CancellationToken ct = default);
    /// <summary>
    /// Sends a heartbeat for the active session. Returns <c>true</c> only when
    /// the call actually round-tripped to the server; returns <c>false</c> when
    /// the implementation short-circuited (e.g. transport not Connected, future
    /// rate-limiter / circuit-breaker). Callers should treat <c>false</c> the
    /// same as a thrown exception — the ping did not happen.
    /// </summary>
    Task<bool> HeartbeatAsync(Guid sessionId, CancellationToken ct = default);
}
