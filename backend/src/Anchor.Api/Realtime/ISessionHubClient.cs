namespace Anchor.Api.Realtime;

public interface ISessionHubClient
{
    Task SessionStarted(SessionStartedPayload payload);
    Task SessionEnded(Guid sessionId);
    Task BundleUpdated(BundleUpdatedPayload payload);
}
