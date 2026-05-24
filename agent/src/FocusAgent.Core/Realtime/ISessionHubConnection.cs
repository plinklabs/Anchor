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
}
