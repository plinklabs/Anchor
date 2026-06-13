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
/// </summary>
public sealed class HeartbeatTracker
{
    private readonly ConcurrentDictionary<ParticipantKey, Entry> _entries = new();

    public void Record(Guid sessionId, Guid userId, DateTimeOffset at, WitnessSource source = WitnessSource.Agent)
    {
        var key = new ParticipantKey(sessionId, userId, source);
        _entries.AddOrUpdate(
            key,
            _ => new Entry(at, Reported: false),
            (_, existing) => existing with { LastSeenAt = at });
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
            .Select(kv => new TrackedParticipant(kv.Key.SessionId, kv.Key.UserId, kv.Key.Source, kv.Value.LastSeenAt, kv.Value.Reported))
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

    public void ClearSession(Guid sessionId)
    {
        // Drops every witness source for the session — the key includes Source,
        // so filtering on SessionId alone sweeps both the agent and extension
        // entries.
        foreach (var key in _entries.Keys.Where(k => k.SessionId == sessionId).ToArray())
        {
            _entries.TryRemove(key, out _);
        }
    }

    public readonly record struct ParticipantKey(Guid SessionId, Guid UserId, WitnessSource Source);

    public readonly record struct Entry(DateTimeOffset LastSeenAt, bool Reported);
}

public sealed record TrackedParticipant(Guid SessionId, Guid UserId, WitnessSource Source, DateTimeOffset LastSeenAt, bool Reported);
