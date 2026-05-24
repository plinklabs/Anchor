using System.Collections.Concurrent;
using Anchor.Api.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace Anchor.Api.Tests.FakeAuth;

public sealed class RecordingSessionBroadcaster : ISessionBroadcaster
{
    private readonly IHubContext<SessionHub, ISessionHubClient> _hub;

    public RecordingSessionBroadcaster(IHubContext<SessionHub, ISessionHubClient> hub)
    {
        _hub = hub;
    }

    public ConcurrentBag<SessionStartedCall> SessionStartedCalls { get; } = new();
    public ConcurrentBag<Guid> SessionEndedCalls { get; } = new();
    public ConcurrentBag<BundleUpdatedPayload> BundleUpdatedCalls { get; } = new();

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
        return _hub.Clients.Group(SessionHub.GroupName(sessionId)).SessionEnded(sessionId);
    }

    public Task BundleUpdatedAsync(BundleUpdatedPayload payload, CancellationToken cancellationToken = default)
    {
        BundleUpdatedCalls.Add(payload);
        return _hub.Clients.Group(SessionHub.GroupName(payload.SessionId)).BundleUpdated(payload);
    }
}

public sealed record SessionStartedCall(SessionStartedPayload Payload, IReadOnlyList<Guid> RecipientUserIds)
{
    public Guid SessionId => Payload.SessionId;
    public string JoinCode => Payload.JoinCode;
}
