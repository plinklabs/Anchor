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
}

internal sealed class SessionBroadcaster : ISessionBroadcaster
{
    private readonly IHubContext<SessionHub, ISessionHubClient> _hub;

    public SessionBroadcaster(IHubContext<SessionHub, ISessionHubClient> hub)
    {
        _hub = hub;
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
        => _hub.Clients.Group(SessionHub.GroupName(sessionId)).SessionEnded(sessionId);

    public Task BundleUpdatedAsync(BundleUpdatedPayload payload, CancellationToken cancellationToken = default)
        => _hub.Clients.Group(SessionHub.GroupName(payload.SessionId)).BundleUpdated(payload);
}
