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
    public required string JoinCode { get; init; }

    public User? Teacher { get; init; }
    public Class? Class { get; init; }
    public ICollection<SessionBundle> SessionBundles { get; init; } = new List<SessionBundle>();
    public ICollection<SessionParticipant> Participants { get; init; } = new List<SessionParticipant>();
}
