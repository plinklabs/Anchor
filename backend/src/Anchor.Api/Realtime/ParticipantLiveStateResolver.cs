using Microsoft.Extensions.Options;

namespace Anchor.Api.Realtime;

/// <summary>
/// Resolves a participant's teacher-facing <see cref="ParticipantLiveState"/>
/// from its lifecycle timestamps plus in-memory heartbeat liveness (#100).
/// Single home for the heartbeat-stale threshold so the GET roster snapshot and
/// the <see cref="HeartbeatMonitor"/>'s live transitions stay in lockstep — both
/// read <see cref="HeartbeatOptions.Timeout"/>.
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
        // Precedence mirrors the realistic transition order: a Left row wins
        // over the JoinedAt that preceded it; a join after a decline (JoinedAt
        // set, never cleared) reads as Joined, not Declined.
        if (leftAt is not null)
            return ParticipantLiveState.Left;

        if (joinedAt is not null)
        {
            // The tracker only holds an entry once the agent has pinged at least
            // once, and the monitor never reports a never-pinged joiner as lost —
            // so treat "joined, not (yet) tracked" as fresh to keep the snapshot
            // consistent with the live HeartbeatLost signal.
            if (_heartbeats.TryGet(sessionId, userId, out var entry) &&
                (_clock.GetUtcNow() - entry.LastSeenAt) > _options.CurrentValue.Timeout)
            {
                return ParticipantLiveState.HeartbeatStale;
            }

            return ParticipantLiveState.Joined;
        }

        if (declinedAt is not null)
            return ParticipantLiveState.Declined;

        return ParticipantLiveState.NeverJoined;
    }
}
