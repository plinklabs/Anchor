using Anchor.Domain.Sessions;
using Anchor.Domain.Users;

namespace Anchor.Domain.Events;

/// <summary>
/// Per-(session, user, kind) aggregate of <see cref="Event"/> rows. Written
/// when a session ends so that raw events can be pruned after 30 days while
/// retaining the "47 foreground changes, 12 blocked URLs" counts indefinitely
/// for reporting.
/// </summary>
public sealed class SessionEventSummary
{
    public required Guid SessionId { get; init; }
    public required Guid UserId { get; init; }
    public required EventKind Kind { get; init; }
    public required int Count { get; set; }
    public required DateTimeOffset FirstAt { get; set; }
    public required DateTimeOffset LastAt { get; set; }

    public Session? Session { get; init; }
    public User? User { get; init; }
}
