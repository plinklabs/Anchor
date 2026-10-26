using Microsoft.Extensions.Options;

namespace Anchor.Api.Realtime;

/// <summary>
/// Resolves a participant's teacher-facing <see cref="ParticipantLiveState"/>
/// from its lifecycle timestamps plus in-memory heartbeat liveness (#100).
/// Single home for the heartbeat-stale threshold so the GET roster snapshot and
/// the <see cref="HeartbeatMonitor"/>'s live transitions stay in lockstep — both
/// read <see cref="HeartbeatOptions.Timeout"/>, and, for an agent that hasn't
/// pinged yet, <see cref="HeartbeatOptions.AgentJoinGrace"/> (#444).
/// </summary>
public sealed class ParticipantLiveStateResolver
{
    private readonly HeartbeatTracker _heartbeats;
    private readonly IOptionsMonitor<HeartbeatOptions> _options;
    private readonly TimeProvider _clock;

    public ParticipantLiveStateResolver(
        HeartbeatTracker heartbeats,
        IOptionsMonitor<HeartbeatOptions> options,
        TimeProvider clock)
    {
        _heartbeats = heartbeats;
        _options = options;
        _clock = clock;
    }

    public ParticipantLiveState Resolve(
        Guid sessionId,
        Guid userId,
        DateTimeOffset? joinedAt,
        DateTimeOffset? declinedAt,
        DateTimeOffset? leftAt)
    {
        // A Left row wins over the JoinedAt that preceded it.
        if (leftAt is not null)
            return ParticipantLiveState.Left;

        // Of a join and a decline, the later one is the student's answer. The
        // extension joins on SessionStarted without waiting for the student's
        // answer in the agent, so a decline usually lands on a row it already
        // joined, and reads as Declined, as the hub broadcast it and as the
        // backend treats it (#471). A tie is a decline too: JoinSession doesn't
        // join a declined row, so a join is never recorded after a decline
        // that stays set. Joining again by code clears the decline.
        if (declinedAt is not null && (joinedAt is null || declinedAt >= joinedAt))
            return ParticipantLiveState.Declined;

        if (joinedAt is not null)
        {
            // Mirrors what the monitor reports. Its agent entry appears when the
            // participant joins, awaiting the agent's first ping: past the join
            // grace with none, the agent is missing, as when only the extension
            // joined (#444). Once the agent has pinged, silence past the timeout
            // is a stale heartbeat. A joiner with no entry at all (right after a
            // backend restart, until their agent or extension pings) is one the
            // monitor can't report, so it reads as joined.
            if (_heartbeats.TryGet(sessionId, userId, out var entry))
            {
                var options = _options.CurrentValue;
                var age = _clock.GetUtcNow() - entry.LastSeenAt;
                if (entry.AwaitingFirstPing)
                {
                    if (age > options.AgentJoinGrace)
                        return ParticipantLiveState.AgentMissing;
                }
                else if (age > options.Timeout)
                {
                    return ParticipantLiveState.HeartbeatStale;
                }
            }

            return ParticipantLiveState.Joined;
        }

        return ParticipantLiveState.NeverJoined;
    }
}
