using Anchor.Domain.Classes;
using Anchor.Domain.Users;

namespace Anchor.Domain.Sessions;

public sealed class Session
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid TeacherId { get; init; }
    public required Guid ClassId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>
    /// When the session is planned to end (#447): the end of a lesson period or
    /// a time the teacher picked when starting it. The backend ends it then,
    /// as if the teacher had clicked End; the teacher can still end it earlier.
    /// Null for a session started without a planned end, which only the
    /// MaxDuration safety net (#345) ends on its own.
    /// </summary>
    public DateTimeOffset? EndsAt { get; init; }

    public required string JoinCode { get; init; }

    public User? Teacher { get; init; }
    public Class? Class { get; init; }
    public ICollection<SessionBundle> SessionBundles { get; init; } = new List<SessionBundle>();
    public ICollection<SessionParticipant> Participants { get; init; } = new List<SessionParticipant>();

    /// <summary>
    /// Whether a session planned to end at <paramref name="endsAt"/> is past
    /// that end at <paramref name="now"/>. From that moment it is over for
    /// every agent and extension, even in the moment before the backend marks
    /// it ended (#447): nothing may join or rejoin it any more. A static over
    /// the column rather than an instance member because callers project
    /// <see cref="EndsAt"/> out of their queries: SQLite (dev + tests) can't
    /// compare DateTimeOffset values server-side.
    /// </summary>
    public static bool IsPastPlannedEnd(DateTimeOffset? endsAt, DateTimeOffset now) =>
        endsAt is { } end && now >= end;
}
