using System.Collections.Concurrent;
using Anchor.Api.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace Anchor.Api.Tests.FakeAuth;

public sealed class RecordingSessionBroadcaster : ISessionBroadcaster
{
    private readonly IHubContext<SessionHub, ISessionHubClient> _hub;
    private readonly HeartbeatTracker _heartbeats;

    public RecordingSessionBroadcaster(
        IHubContext<SessionHub, ISessionHubClient> hub,
        HeartbeatTracker heartbeats)
    {
        _hub = hub;
        _heartbeats = heartbeats;
    }

    public ConcurrentBag<SessionStartedCall> SessionStartedCalls { get; } = new();
    public ConcurrentBag<Guid> SessionEndedCalls { get; } = new();
    public ConcurrentBag<SessionBundlesUpdatedCall> SessionBundlesUpdatedCalls { get; } = new();
    public ConcurrentBag<ParticipantStateChangedPayload> ParticipantStateChangedCalls { get; } = new();
    public ConcurrentBag<HeartbeatLostPayload> HeartbeatLostCalls { get; } = new();
    public ConcurrentBag<AgentReconnectedPayload> AgentReconnectedCalls { get; } = new();
    public ConcurrentBag<AllowlistAmendedPayload> AllowlistAmendedCalls { get; } = new();
    public ConcurrentBag<UnblockRequestedPayload> UnblockRequestedCalls { get; } = new();

    public Task SessionStartedAsync(
        SessionStartedPayload payload,
        IReadOnlyCollection<Guid> recipientUserIds,
        CancellationToken cancellationToken = default)
    {
        SessionStartedCalls.Add(new SessionStartedCall(payload, recipientUserIds.ToArray()));
        if (recipientUserIds.Count == 0)
            return Task.CompletedTask;
        var groups = recipientUserIds.Select(SessionHub.UserGroupName).ToArray();
        return _hub.Clients.Groups(groups).SessionStarted(payload);
    }

    public Task SessionEndedAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        SessionEndedCalls.Add(sessionId);
        // Mirror production SessionBroadcaster: drop liveness state up-front so
        // tests observing tracker behaviour see the same outcome they would in
        // a real deployment.
        _heartbeats.ClearSession(sessionId);
        return _hub.Clients.Group(SessionHub.GroupName(sessionId)).SessionEnded(sessionId);
    }

    public Task SessionBundlesUpdatedAsync(Guid userId, SessionBundlesUpdatedPayload payload, CancellationToken cancellationToken = default)
    {
        SessionBundlesUpdatedCalls.Add(new SessionBundlesUpdatedCall(userId, payload));
        return _hub.Clients.Group(SessionHub.UserGroupName(userId)).SessionBundlesUpdated(payload);
    }

    public Task ParticipantStateChangedAsync(ParticipantStateChangedPayload payload, CancellationToken cancellationToken = default)
    {
        ParticipantStateChangedCalls.Add(payload);
        return _hub.Clients.Group(SessionHub.GroupName(payload.SessionId)).ParticipantStateChanged(payload);
    }

    public Task HeartbeatLostAsync(HeartbeatLostPayload payload, CancellationToken cancellationToken = default)
    {
        HeartbeatLostCalls.Add(payload);
        return _hub.Clients.Group(SessionHub.GroupName(payload.SessionId)).HeartbeatLost(payload);
    }

    public Task AgentReconnectedAsync(AgentReconnectedPayload payload, CancellationToken cancellationToken = default)
    {
        AgentReconnectedCalls.Add(payload);
        return _hub.Clients.Group(SessionHub.GroupName(payload.SessionId)).AgentReconnected(payload);
    }

    public Task AllowlistAmendedAsync(AllowlistAmendedPayload payload, CancellationToken cancellationToken = default)
    {
        AllowlistAmendedCalls.Add(payload);
        return _hub.Clients.Group(SessionHub.UserGroupName(payload.UserId)).AllowlistAmended(payload);
    }

    public Task UnblockRequestedAsync(UnblockRequestedPayload payload, CancellationToken cancellationToken = default)
    {
        UnblockRequestedCalls.Add(payload);
        return _hub.Clients.Group(SessionHub.GroupName(payload.SessionId)).UnblockRequested(payload);
    }
}

public sealed record SessionStartedCall(SessionStartedPayload Payload, IReadOnlyList<Guid> RecipientUserIds)
{
    public Guid SessionId => Payload.SessionId;
    public string JoinCode => Payload.JoinCode;
}

public sealed record SessionBundlesUpdatedCall(Guid UserId, SessionBundlesUpdatedPayload Payload)
{
    public Guid SessionId => Payload.SessionId;
}
