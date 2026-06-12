namespace Anchor.Domain.Sessions;

/// <summary>
/// A whole-class allowance for a single host during a focus session (#101).
/// Unlike <see cref="SessionUnblockGrant"/> (scoped to one student), this applies
/// to every participant in the session — current and future joiners — so the
/// teacher can open a site "we all need now" in one action. Scoped to the
/// session: when the session ends, the grant becomes irrelevant (the next
/// session starts fresh per the design doc, §7.4).
/// </summary>
public sealed class SessionWideUnblockGrant
{
    public required Guid SessionId { get; init; }

    /// <summary>
    /// Lowercased hostname (no scheme, no path, no port). Stored normalised so
    /// the per-(session, host) primary key dedupes case variants.
    /// </summary>
    public required string Host { get; init; }

    public required DateTimeOffset GrantedAt { get; init; }

    public Session? Session { get; init; }
}
