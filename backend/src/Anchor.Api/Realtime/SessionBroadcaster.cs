using Microsoft.AspNetCore.SignalR;

namespace Anchor.Api.Realtime;

public interface ISessionBroadcaster
{
    Task SessionStartedAsync(
        SessionStartedPayload payload,
        IReadOnlyCollection<Guid> recipientUserIds,
        CancellationToken cancellationToken = default);
    Task SessionEndedAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task BundleUpdatedAsync(BundleUpdatedPayload payload, CancellationToken cancellationToken = default);
    Task HeartbeatLostAsync(HeartbeatLostPayload payload, CancellationToken cancellationToken = default);
    Task AgentReconnectedAsync(AgentReconnectedPayload payload, CancellationToken cancellationToken = default);
}

internal sealed class SessionBroadcaster : ISessionBroadcaster
{
    private readonly IHubContext<SessionHub, ISessionHubClient> _hub;
    private readonly HeartbeatTracker _heartbeats;

    public SessionBroadcaster(
        IHubContext<SessionHub, ISessionHubClient> hub,
        HeartbeatTracker heartbeats)
    {
        _hub = hub;
        _heartbeats = heartbeats;
    }

    public Task SessionStartedAsync(
        SessionStartedPayload payload,
        IReadOnlyCollection<Guid> recipientUserIds,
        CancellationToken cancellationToken = default)
    {
        if (recipientUserIds.Count == 0)
            return Task.CompletedTask;

        var groups = recipientUserIds.Select(SessionHub.UserGroupName).ToArray();
        return _hub.Clients.Groups(groups).SessionStarted(payload);
    }

    public Task SessionEndedAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        // Drop liveness state up-front: no point keeping a participant on the
        // monitor's scan list once the session is over — they'd just stale
        // out and emit spurious HeartbeatLost events after the fact.
        _heartbeats.ClearSession(sessionId);
        return _hub.Clients.Group(SessionHub.GroupName(sessionId)).SessionEnded(sessionId);
    }

    public Task BundleUpdatedAsync(BundleUpdatedPayload payload, CancellationToken cancellationToken = default)
        => _hub.Clients.Group(SessionHub.GroupName(payload.SessionId)).BundleUpdated(payload);

    public Task HeartbeatLostAsync(HeartbeatLostPayload payload, CancellationToken cancellationToken = default)
        => _hub.Clients.Group(SessionHub.GroupName(payload.SessionId)).HeartbeatLost(payload);

    public Task AgentReconnectedAsync(AgentReconnectedPayload payload, CancellationToken cancellationToken = default)
        => _hub.Clients.Group(SessionHub.GroupName(payload.SessionId)).AgentReconnected(payload);
}
