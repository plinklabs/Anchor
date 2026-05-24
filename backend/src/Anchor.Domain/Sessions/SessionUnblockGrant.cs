using Anchor.Domain.Users;

namespace Anchor.Domain.Sessions;

/// <summary>
/// A teacher's per-student allowance for a single host during a focus session.
/// Created when the teacher approves a pending UnblockRequest in the dashboard.
/// Scoped to the session — when the session ends, all grants for it become
/// irrelevant (the next session starts fresh per the design doc, §7.4).
/// </summary>
public sealed class SessionUnblockGrant
{
    public required Guid SessionId { get; init; }
    public required Guid UserId { get; init; }

    /// <summary>
    /// Lowercased hostname (no scheme, no path, no port). Stored normalised
    /// so the per-(session, user, host) primary key dedupes case variants.
    /// </summary>
    public required string Host { get; init; }

    public required DateTimeOffset GrantedAt { get; init; }

    public Session? Session { get; init; }
    public User? User { get; init; }
}
