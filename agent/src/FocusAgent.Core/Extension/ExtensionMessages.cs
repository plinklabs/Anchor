namespace FocusAgent.Core.Extension;

/// <summary>
/// The <c>type</c> of each message on the witness link between the agent and the
/// Edge extension (#489). The extension repeats these strings in
/// <c>extension/src/shared/witness.ts</c>; the native host forwards the messages
/// unchanged, so the two ends must agree on them.
/// </summary>
public static class ExtensionMessages
{
    // ---- agent → extension -------------------------------------------------

    /// <summary>
    /// The student is in a session: its id, class, join code, start and the
    /// domains the extension allows. Sent when the agent has joined it, and again
    /// on every witness (re)connect while the student is in it.
    /// </summary>
    public const string SessionStarted = "session_started";

    /// <summary>
    /// The student is in no session. Sent on a witness (re)connect outside a
    /// session, so an extension that missed the end of one while its link was
    /// down stops enforcing it.
    /// </summary>
    public const string NoSession = "no_session";

    /// <summary>
    /// The session ended for the student: the teacher ended it, or the student
    /// left it.
    /// </summary>
    public const string SessionEnded = "session_ended";

    /// <summary>The teacher changed the session's bundles: the full new domain list (#93).</summary>
    public const string AllowlistReplaced = "allowlist_replaced";

    /// <summary>The teacher approved an unblock request: the domains it adds (#73).</summary>
    public const string AllowlistAmended = "allowlist_amended";

    /// <summary>The answer to an <see cref="UnblockRequest"/>, matched by its <c>requestId</c>.</summary>
    public const string UnblockRequestResult = "unblock_request_result";

    // ---- extension → agent -------------------------------------------------

    /// <summary>The extension blocked a navigation; reported as a <c>BlockedUrl</c> event.</summary>
    public const string BlockedUrl = "blocked_url";

    /// <summary>The student asked for access from the block page; reported as an <c>UnblockRequest</c> event.</summary>
    public const string UnblockRequest = "unblock_request";

    /// <summary>
    /// The extension witnessed tampering in the browser (an InPrivate window, its
    /// host access revoked); reported as a <c>TamperDetected</c> event (#105).
    /// </summary>
    public const string Tamper = "tamper";
}
