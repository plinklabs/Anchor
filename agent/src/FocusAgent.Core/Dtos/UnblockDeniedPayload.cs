namespace FocusAgent.Core.Dtos;

/// <summary>
/// Wire-shape twin of the backend's <c>UnblockDeniedPayload</c> (#523): the
/// teacher turned down this student's request for <see cref="Host"/>. Pushed to
/// the student's user group. Nothing changes on the allowlist; the agent hands
/// it down to the extension over the witness link, so the block page says the
/// request was declined and lets the student ask again.
/// </summary>
public sealed record UnblockDeniedPayload(
    Guid SessionId,
    Guid UserId,
    string Host,
    DateTimeOffset DeniedAt);
