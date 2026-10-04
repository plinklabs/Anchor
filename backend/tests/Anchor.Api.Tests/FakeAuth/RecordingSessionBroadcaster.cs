using System.Collections.Concurrent;
using Anchor.Api.Realtime;

namespace Anchor.Api.Tests.FakeAuth;

/// <summary>
/// Records every broadcast, then hands it to the production
/// <see cref="ISessionBroadcaster"/> (<see cref="AnchorApiFactory"/> passes in
/// the one Program.cs registers), so a hub test's connections receive exactly
/// what a real client would. It doesn't route anything itself: a copy of the
/// routing here would let a test pass on the copy while production sends a
/// message somewhere else (#366).
/// </summary>
public sealed class RecordingSessionBroadcaster : ISessionBroadcaster
{
    private readonly ISessionBroadcaster _inner;

    public RecordingSessionBroadcaster(ISessionBroadcaster inner)
    {
        _inner = inner;
    }

    public ConcurrentBag<SessionStartedCall> SessionStartedCalls { get; } = new();
    public ConcurrentBag<SessionEndedCall> SessionEndedCalls { get; } = new();
    public ConcurrentBag<SessionBundlesUpdatedCall> SessionBundlesUpdatedCalls { get; } = new();
    public ConcurrentBag<ParticipantStateChangedPayload> ParticipantStateChangedCalls { get; } = new();
    public ConcurrentBag<HeartbeatLostPayload> HeartbeatLostCalls { get; } = new();
    public ConcurrentBag<AgentReconnectedPayload> AgentReconnectedCalls { get; } = new();
    public ConcurrentBag<AllowlistAmendedPayload> AllowlistAmendedCalls { get; } = new();
    public ConcurrentBag<UnblockRequestedPayload> UnblockRequestedCalls { get; } = new();
    public ConcurrentBag<TamperDetectedPayload> TamperDetectedCalls { get; } = new();

    public Task SessionStartedAsync(
        SessionStartedPayload payload,
        IReadOnlyCollection<Guid> recipientUserIds,
        CancellationToken cancellationToken = default)
    {
        SessionStartedCalls.Add(new SessionStartedCall(payload, recipientUserIds.ToArray()));
        return _inner.SessionStartedAsync(payload, recipientUserIds, cancellationToken);
    }

    public Task SessionEndedAsync(
        Guid sessionId,
        IReadOnlyCollection<Guid> recipientUserIds,
        CancellationToken cancellationToken = default)
    {
        SessionEndedCalls.Add(new SessionEndedCall(sessionId, recipientUserIds.ToArray()));
        return _inner.SessionEndedAsync(sessionId, recipientUserIds, cancellationToken);
    }

    public Task SessionBundlesUpdatedAsync(Guid userId, SessionBundlesUpdatedPayload payload, CancellationToken cancellationToken = default)
    {
        SessionBundlesUpdatedCalls.Add(new SessionBundlesUpdatedCall(userId, payload));
        return _inner.SessionBundlesUpdatedAsync(userId, payload, cancellationToken);
    }

    public Task ParticipantStateChangedAsync(ParticipantStateChangedPayload payload, CancellationToken cancellationToken = default)
    {
        ParticipantStateChangedCalls.Add(payload);
        return _inner.ParticipantStateChangedAsync(payload, cancellationToken);
    }

    public Task HeartbeatLostAsync(HeartbeatLostPayload payload, CancellationToken cancellationToken = default)
    {
        HeartbeatLostCalls.Add(payload);
        return _inner.HeartbeatLostAsync(payload, cancellationToken);
    }

    public Task AgentReconnectedAsync(AgentReconnectedPayload payload, CancellationToken cancellationToken = default)
    {
        AgentReconnectedCalls.Add(payload);
        return _inner.AgentReconnectedAsync(payload, cancellationToken);
    }

    public Task AllowlistAmendedAsync(AllowlistAmendedPayload payload, CancellationToken cancellationToken = default)
    {
        AllowlistAmendedCalls.Add(payload);
        return _inner.AllowlistAmendedAsync(payload, cancellationToken);
    }

    public Task UnblockRequestedAsync(UnblockRequestedPayload payload, CancellationToken cancellationToken = default)
    {
        UnblockRequestedCalls.Add(payload);
        return _inner.UnblockRequestedAsync(payload, cancellationToken);
    }

    public Task TamperDetectedAsync(TamperDetectedPayload payload, CancellationToken cancellationToken = default)
    {
        TamperDetectedCalls.Add(payload);
        return _inner.TamperDetectedAsync(payload, cancellationToken);
    }
}

public sealed record SessionStartedCall(SessionStartedPayload Payload, IReadOnlyList<Guid> RecipientUserIds)
{
    public Guid SessionId => Payload.SessionId;
    public string JoinCode => Payload.JoinCode;
}

public sealed record SessionEndedCall(Guid SessionId, IReadOnlyList<Guid> RecipientUserIds);

public sealed record SessionBundlesUpdatedCall(Guid UserId, SessionBundlesUpdatedPayload Payload)
{
    public Guid SessionId => Payload.SessionId;
}
