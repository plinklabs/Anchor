namespace Anchor.Api.Sessions;

/// <summary>
/// The planned ends (#447) of the running sessions this backend knows of, kept
/// in memory so that <see cref="SessionAutoEnder"/> ends each session at its
/// <c>EndsAt</c> rather than at its next sweep, minutes later.
///
/// <para>
/// Starting a session with a planned end puts it here. So does anything that
/// reads a running session's planned end — a sweep, an agent or extension
/// joining or asking whether it is still in the session — because the
/// schedule starts out empty when the backend (re)starts, and a session whose
/// planned end has already passed is then ended at once rather than at the
/// next sweep. Ending a session takes it off.
/// </para>
/// </summary>
public sealed class SessionEndSchedule
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, DateTimeOffset> _endsAt = new();
    private TaskCompletionSource _changed = NewSignal();

    /// <summary>
    /// Completes the next time a session is scheduled, so a waiting
    /// <see cref="SessionAutoEnder"/> can wake for a planned end that comes
    /// before the one it waits for. Read it before <see cref="Earliest"/>, so
    /// that a session scheduled between the two still wakes the waiter.
    /// </summary>
    public Task WhenChanged
    {
        get
        {
            lock (_gate)
                return _changed.Task;
        }
    }

    /// <summary>The earliest planned end on the schedule, or null when it's empty.</summary>
    public DateTimeOffset? Earliest
    {
        get
        {
            lock (_gate)
                return _endsAt.Count == 0 ? null : _endsAt.Values.Min();
        }
    }

    /// <summary>Schedules the end of a running session at <paramref name="endsAt"/>.</summary>
    public void Schedule(Guid sessionId, DateTimeOffset endsAt)
    {
        TaskCompletionSource changed;
        lock (_gate)
        {
            if (_endsAt.TryGetValue(sessionId, out var known) && known == endsAt)
                return;
            _endsAt[sessionId] = endsAt;
            changed = _changed;
            _changed = NewSignal();
        }
        changed.TrySetResult();
    }

    /// <summary>Takes an ended session off the schedule.</summary>
    public void Remove(Guid sessionId)
    {
        lock (_gate)
            _endsAt.Remove(sessionId);
    }

    /// <summary>
    /// Takes every session whose planned end is at or before
    /// <paramref name="now"/> off the schedule and returns their ids, for the
    /// caller to end. One that fails to end is picked up by the next sweep.
    /// </summary>
    public IReadOnlyList<Guid> TakeDue(DateTimeOffset now)
    {
        lock (_gate)
        {
            var due = _endsAt.Where(e => e.Value <= now).Select(e => e.Key).ToList();
            foreach (var sessionId in due)
                _endsAt.Remove(sessionId);
            return due;
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
