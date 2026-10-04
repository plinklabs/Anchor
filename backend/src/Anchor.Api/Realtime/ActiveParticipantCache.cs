using System.Collections.Concurrent;
using System.Linq.Expressions;
using Anchor.Domain.Sessions;
using Microsoft.EntityFrameworkCore;

namespace Anchor.Api.Realtime;

/// <summary>
/// In-memory answer to "is this user an active participant of this session?",
/// the check behind every <c>Heartbeat</c> / <c>ExtensionHeartbeat</c> and
/// <c>ReportEvent</c> (#342). Agent and extension each ping every 10 s, so at
/// ~300 students a database query per ping was ~60 queries per second — by far
/// the largest share of database load. "Active" means a
/// <see cref="SessionParticipant"/> row with <c>JoinedAt</c> set and
/// <c>LeftAt</c> null, in a session that hasn't ended (<see cref="IsActive"/>).
/// The query this replaced didn't check the session: an agent or extension that
/// never heard its session end could ping it back into the heartbeat tracker,
/// and the monitor would then report the student lost, or the extension
/// silent, after the session was over (#354).
///
/// <para>
/// Every code path that changes those two columns reports the row it just
/// saved through <see cref="Update"/>: <c>JoinSession</c> (including a rejoin),
/// <c>LeaveSession</c> (which also covers a ManualLeave — the agent reports that
/// event and then leaves), the AgentKilled event and the join-by-code rejoin.
/// Ending a session drops its entries through <see cref="ClearSession"/>.
/// </para>
///
/// <para>
/// A miss — the normal case right after a backend restart, when nothing is
/// cached yet but agents keep pinging without rejoining — falls back to the
/// database once and caches the answer, so a restart never rejects a legitimate
/// heartbeat. Entries also expire, as a backstop and to keep memory bounded:
/// <see cref="ActiveEntryLifetime"/> for active pairs (kept current by the write
/// paths above, so this costs one query per pair per lifetime) and the much
/// shorter <see cref="InactiveEntryLifetime"/> for inactive ones, which are rare
/// and could otherwise pile up from pings for arbitrary session ids.
/// </para>
///
/// <para>
/// Singleton and in-process, like <see cref="HeartbeatTracker"/>: the backend
/// runs as a single instance, so this adds no new scaling constraint.
/// </para>
/// </summary>
public sealed class ActiveParticipantCache
{
    public static readonly TimeSpan ActiveEntryLifetime = TimeSpan.FromHours(1);
    public static readonly TimeSpan InactiveEntryLifetime = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<(Guid SessionId, Guid UserId), Entry> _entries = new();
    private readonly TimeProvider _clock;

    // Bumped by every Update. A database lookup snapshots it before querying, so
    // it can tell whether an Update landed while its query was in flight — the
    // Update is then authoritative, and the lookup's possibly older answer must
    // not overwrite it.
    private long _updateSequence;
    private long _nextSweepTicks;

    public ActiveParticipantCache(TimeProvider clock)
    {
        _clock = clock;
        _nextSweepTicks = clock.GetUtcNow().Add(InactiveEntryLifetime).UtcTicks;
    }

    /// <summary>
    /// The database form of "active": the user's participant row in the
    /// session has <c>JoinedAt</c> set and <c>LeftAt</c> null, and the session
    /// hasn't ended.
    /// </summary>
    public static Expression<Func<SessionParticipant, bool>> IsActive(Guid sessionId, Guid userId) =>
        p => p.SessionId == sessionId &&
             p.UserId == userId &&
             p.JoinedAt != null &&
             p.LeftAt == null &&
             p.Session!.EndedAt == null;

    /// <summary>
    /// Answers from memory when it can; otherwise queries
    /// <paramref name="participants"/> once and caches the result.
    /// </summary>
    public ValueTask<bool> IsActiveAsync(
        IQueryable<SessionParticipant> participants,
        Guid sessionId,
        Guid userId,
        CancellationToken ct) =>
        IsActiveAsync(
            sessionId,
            userId,
            token => participants.AnyAsync(IsActive(sessionId, userId), token),
            ct);

    /// <summary>
    /// Answers from memory when it can; otherwise runs
    /// <paramref name="lookup"/> (the database query) once and caches the result.
    /// </summary>
    public async ValueTask<bool> IsActiveAsync(
        Guid sessionId,
        Guid userId,
        Func<CancellationToken, Task<bool>> lookup,
        CancellationToken ct)
    {
        SweepIfDue(_clock.GetUtcNow());

        if (TryGet(sessionId, userId, out var cached))
            return cached;

        var startSequence = Interlocked.Read(ref _updateSequence);
        var isActive = await lookup(ct);

        var looked = new Entry(isActive, _clock.GetUtcNow(), startSequence);
        _entries.AddOrUpdate(
            (sessionId, userId),
            looked,
            (_, existing) => existing.Sequence > startSequence ? existing : looked);
        return isActive;
    }

    /// <summary>
    /// The cached answer for a pair, if there is one that hasn't expired. Does
    /// not touch the database.
    /// </summary>
    public bool TryGet(Guid sessionId, Guid userId, out bool isActive)
    {
        if (_entries.TryGetValue((sessionId, userId), out var entry) && !IsExpired(entry, _clock.GetUtcNow()))
        {
            isActive = entry.IsActive;
            return true;
        }

        isActive = false;
        return false;
    }

    /// <summary>
    /// Records the participant row a write path has just saved. Call it after
    /// <c>SaveChanges</c>, so that a concurrent database lookup either already
    /// sees the new row or is overruled by this entry.
    /// </summary>
    public void Update(SessionParticipant participant)
    {
        var sequence = Interlocked.Increment(ref _updateSequence);
        var isActive = participant.JoinedAt is not null && participant.LeftAt is null;
        _entries[(participant.SessionId, participant.UserId)] =
            new Entry(isActive, _clock.GetUtcNow(), sequence);
    }

    /// <summary>Drops every cached pair of a session that has ended.</summary>
    public void ClearSession(Guid sessionId)
    {
        foreach (var key in _entries.Keys.Where(k => k.SessionId == sessionId).ToArray())
            _entries.TryRemove(key, out _);
    }

    /// <summary>Number of pairs held in memory, expired or not.</summary>
    public int Count => _entries.Count;

    private static bool IsExpired(Entry entry, DateTimeOffset now) =>
        now - entry.StoredAt >= (entry.IsActive ? ActiveEntryLifetime : InactiveEntryLifetime);

    // Expired entries already read as misses; this only reclaims their memory.
    // Runs at most once per InactiveEntryLifetime, on whichever lookup is first
    // past the due time.
    private void SweepIfDue(DateTimeOffset now)
    {
        var due = Interlocked.Read(ref _nextSweepTicks);
        if (now.UtcTicks < due)
            return;
        if (Interlocked.CompareExchange(ref _nextSweepTicks, now.Add(InactiveEntryLifetime).UtcTicks, due) != due)
            return;

        foreach (var entry in _entries)
        {
            // Value-matched removal: an entry rewritten since we read it stays.
            if (IsExpired(entry.Value, now))
                _entries.TryRemove(entry);
        }
    }

    private readonly record struct Entry(bool IsActive, DateTimeOffset StoredAt, long Sequence);
}
