namespace Anchor.Api.Realtime;

public interface ISessionHubClient
{
    Task SessionStarted(SessionStartedPayload payload);
    Task SessionEnded(Guid sessionId);
    Task BundleUpdated(BundleUpdatedPayload payload);
    Task HeartbeatLost(HeartbeatLostPayload payload);
    Task AgentReconnected(AgentReconnectedPayload payload);
    Task AllowlistAmended(AllowlistAmendedPayload payload);
    Task UnblockRequested(UnblockRequestedPayload payload);
}
