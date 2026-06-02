namespace Anchor.Api.Realtime;

public interface ISessionHubClient
{
    Task SessionStarted(SessionStartedPayload payload);
    Task SessionEnded(Guid sessionId);
    Task SessionBundlesUpdated(SessionBundlesUpdatedPayload payload);
    Task ParticipantStateChanged(ParticipantStateChangedPayload payload);
    Task HeartbeatLost(HeartbeatLostPayload payload);
    Task AgentReconnected(AgentReconnectedPayload payload);
    Task AllowlistAmended(AllowlistAmendedPayload payload);
    Task UnblockRequested(UnblockRequestedPayload payload);
}
