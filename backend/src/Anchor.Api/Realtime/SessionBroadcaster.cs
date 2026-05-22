using Microsoft.AspNetCore.SignalR;

namespace Anchor.Api.Realtime;

public interface ISessionBroadcaster
{
    Task SessionStartedAsync(SessionStartedPayload payload, CancellationToken cancellationToken = default);
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

    public Task SessionStartedAsync(SessionStartedPayload payload, CancellationToken cancellationToken = default)
        => _hub.Clients.Group(SessionHub.GroupName(payload.SessionId)).SessionStarted(payload);

    public Task SessionEndedAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => _hub.Clients.Group(SessionHub.GroupName(sessionId)).SessionEnded(sessionId);

    public Task BundleUpdatedAsync(BundleUpdatedPayload payload, CancellationToken cancellationToken = default)
        => _hub.Clients.Group(SessionHub.GroupName(payload.SessionId)).BundleUpdated(payload);
}
