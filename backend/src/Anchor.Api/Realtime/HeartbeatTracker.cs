using System.Collections.Concurrent;

namespace Anchor.Api.Realtime;

/// <summary>
/// In-memory liveness state for active session participants. Heartbeats are
/// volatile by design — at 10s ping × N students × 50min lessons, persisting
/// every ping would dwarf the real event stream. We only persist the
/// transitions (lost / reconnected), so the source of truth for "is this
/// agent currently pinging?" lives here.
///
/// Entries are keyed by witness <see cref="WitnessSource"/> as well as
/// participant: the agent and extension share a <c>userId</c>, and tracking them
/// separately is what stops one witness's pings from masking the other's
/// silence (#149).
///
/// <para>
/// An agent entry can also exist before the agent has pinged at all, once
/// <see cref="ExpectAgent"/> says it should be there (#444). Both the agent and
/// the extension join a session, so a join alone doesn't show the agent took
/// part; an entry that only ever waits for its first ping is how the
/// <see cref="HeartbeatMonitor"/> notices an agent that never showed up.
/// </para>
///
/// <para>
/// One step before that, it keeps the class members a session that just
/// started is waiting on to join at all (#493), from <see cref="ExpectJoins"/>
/// until the <see cref="HeartbeatMonitor"/> has looked at them once the join
/// grace has passed. The participant rows say who joined; this only says whom
/// to look at, and when, so the monitor never has to poll the database for
/// sessions that are waiting on no one.
/// </para>
/// </summary>
public sealed class HeartbeatTracker
{
    private readonly ConcurrentDictionary<ParticipantKey, Entry> _entries = new();
    private readonly ConcurrentDictionary<(Guid SessionId, Guid UserId), DateTimeOffset> _awaitedJoins = new();

    public void Record(Guid sessionId, Guid userId, DateTimeOffset at, WitnessSource source = WitnessSource.Agent)
    {
        var key = new ParticipantKey(sessionId, userId, source);
        _entries.AddOrUpdate(
            key,
            _ => new Entry(at, Reported: false),
            // Keeps Reported: an agent reported missing that now pings goes
            // through the monitor's resumed path, like one reported lost.
            (_, existing) => existing with { LastSeenAt = at, AwaitingFirstPing = false });
    }

    /// <summary>
    /// Starts waiting for the participant's agent, from <paramref name="since"/>,
    /// unless it is already tracked (#444). Called when the participant joins
    /// and on each of their extension's pings, so the wait also starts after a
    /// backend restart, when an extension keeps pinging without joining again.
    /// An agent that has pinged, or is already awaited, is left as it is: the
    /// extension joining after the agent never makes it look absent, and a
    /// later call never restarts the wait.
    /// </summary>
    public void ExpectAgent(Guid sessionId, Guid userId, DateTimeOffset since)
    {
        _entries.TryAdd(
            new ParticipantKey(sessionId, userId, WitnessSource.Agent),
            new Entry(since, Reported: false, AwaitingFirstPing: true));
    }

    /// <summary>
    /// Starts waiting for the class members of a session that just started to
    /// join it, from <paramref name="since"/>, the session's start (#493).
    /// </summary>
    public void ExpectJoins(Guid sessionId, IEnumerable<Guid> userIds, DateTimeOffset since)
    {
        foreach (var userId in userIds)
            _awaitedJoins.TryAdd((sessionId, userId), since);
    }

    /// <summary>The joins still awaited, each with when it was first expected (#493).</summary>
    public IReadOnlyList<AwaitedJoin> AwaitedJoins()
    {
        return _awaitedJoins
            .Select(kv => new AwaitedJoin(kv.Key.SessionId, kv.Key.UserId, kv.Value))
            .ToArray();
    }

    /// <summary>
    /// Stops waiting for the participant to join (#493), once the monitor has
    /// looked at them.
    /// </summary>
    public void StopAwaitingJoin(Guid sessionId, Guid userId)
    {
        _awaitedJoins.TryRemove((sessionId, userId), out _);
    }

    public bool TryGet(Guid sessionId, Guid userId, out Entry entry, WitnessSource source = WitnessSource.Agent)
    {
        var ok = _entries.TryGetValue(new ParticipantKey(sessionId, userId, source), out var found);
        entry = found;
        return ok;
    }

    public IReadOnlyList<TrackedParticipant> Snapshot()
    {
        return _entries
            .Select(kv => new TrackedParticipant(
                kv.Key.SessionId, kv.Key.UserId, kv.Key.Source, kv.Value.LastSeenAt, kv.Value.Reported, kv.Value.AwaitingFirstPing))
            .ToArray();
    }

    public void MarkReported(Guid sessionId, Guid userId, WitnessSource source = WitnessSource.Agent)
    {
        var key = new ParticipantKey(sessionId, userId, source);
        _entries.AddOrUpdate(
            key,
            _ => new Entry(DateTimeOffset.MinValue, Reported: true),
            (_, existing) => existing with { Reported = true });
    }

    public void ClearReported(Guid sessionId, Guid userId, WitnessSource source = WitnessSource.Agent)
    {
        var key = new ParticipantKey(sessionId, userId, source);
        _entries.AddOrUpdate(
            key,
            _ => new Entry(DateTimeOffset.MinValue, Reported: false),
            (_, existing) => existing with { Reported = false });
    }

    /// <summary>
    /// Drops every witness source for a single participant, and the wait for
    /// them to join. Used when a participant stops taking part:
    /// <c>LeaveSession</c>, <c>DeclineSession</c>
    /// and the #110 AgentKilled event (#448). Once they're gone their agent's
    /// and extension's last pings must not age into a spurious
    /// <c>HeartbeatLost</c> or <c>extension_silent</c>. A still-running
    /// extension can't record again once the participant has left, because the
    /// hub's heartbeat methods reject a left participant; a ping that slips in
    /// just before is dropped by <see cref="HeartbeatMonitor"/>, which reports
    /// only active participants.
    /// </summary>
    public void Clear(Guid sessionId, Guid userId)
    {
        foreach (var key in _entries.Keys
                     .Where(k => k.SessionId == sessionId && k.UserId == userId)
                     .ToArray())
        {
            _entries.TryRemove(key, out _);
        }

        _awaitedJoins.TryRemove((sessionId, userId), out _);
    }

    public void ClearSession(Guid sessionId)
    {
        // Drops every witness source for the session — the key includes Source,
        // so filtering on SessionId alone sweeps both the agent and extension
        // entries — and the joins it still waits on.
        foreach (var key in _entries.Keys.Where(k => k.SessionId == sessionId).ToArray())
        {
            _entries.TryRemove(key, out _);
        }

        foreach (var key in _awaitedJoins.Keys.Where(k => k.SessionId == sessionId).ToArray())
        {
            _awaitedJoins.TryRemove(key, out _);
        }
    }

    public readonly record struct ParticipantKey(Guid SessionId, Guid UserId, WitnessSource Source);

    /// <summary>
    /// One witness's liveness. While <see cref="AwaitingFirstPing"/> is true the
    /// witness hasn't pinged yet, and <see cref="LastSeenAt"/> is when it was
    /// first expected (#444).
    /// </summary>
    public readonly record struct Entry(DateTimeOffset LastSeenAt, bool Reported, bool AwaitingFirstPing = false);
}

/// <summary>
/// One tracked witness, as <see cref="HeartbeatTracker.Snapshot"/> returns it.
/// See <see cref="HeartbeatTracker.Entry"/> for <see cref="AwaitingFirstPing"/>.
/// </summary>
public sealed record TrackedParticipant(
    Guid SessionId,
    Guid UserId,
    WitnessSource Source,
    DateTimeOffset LastSeenAt,
    bool Reported,
    bool AwaitingFirstPing = false);

/// <summary>
/// A class member a session waits on to join, as
/// <see cref="HeartbeatTracker.AwaitedJoins"/> returns it (#493).
/// <see cref="Since"/> is the session's start.
/// </summary>
public sealed record AwaitedJoin(Guid SessionId, Guid UserId, DateTimeOffset Since);
