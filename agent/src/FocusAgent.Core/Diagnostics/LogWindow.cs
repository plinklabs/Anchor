namespace FocusAgent.Core.Diagnostics;

/// <summary>
/// A session window the backend asked for (#446): the agent uploads its log
/// lines from <see cref="From"/> to <see cref="To"/> (both inclusive), and none
/// from outside it. While the session's window is still open, <see cref="To"/>
/// is the moment the backend answered.
/// </summary>
public sealed record LogWindow(Guid SessionId, DateTimeOffset From, DateTimeOffset To)
{
    public bool Contains(DateTimeOffset at) => at >= From && at <= To;
}

/// <summary>
/// The backend's answer to "which windows still need an upload": the windows,
/// oldest first, and the largest upload it takes per window (gzipped bytes).
/// Switched off, it lists none.
/// </summary>
public sealed record PendingLogWindows(int MaxBytes, IReadOnlyList<LogWindow> Windows);

/// <summary>What the backend did with one upload.</summary>
public enum LogUploadOutcome
{
    /// <summary>Stored.</summary>
    Stored,

    /// <summary>The backend already holds the whole window; nothing to do.</summary>
    AlreadyComplete,

    /// <summary>
    /// Refused for good (uploads switched off in the meantime, the window no
    /// longer asked for, too large): the backend decides on the next connect.
    /// </summary>
    Refused,
}

/// <summary>
/// The backend's log-upload endpoints (#446): <c>GET /log-uploads/pending</c>
/// and <c>POST /log-uploads/{sessionId}</c>. An interface so the Core-side
/// <see cref="LogUploadService"/> is unit-testable without an HTTP stack; the
/// App project provides the HttpClient-backed implementation. Both throw on a
/// network failure or an unexpected status, which the service treats as "try
/// again on the next connect".
/// </summary>
public interface ILogUploadClient
{
    Task<PendingLogWindows> GetPendingAsync(CancellationToken ct = default);

    Task<LogUploadOutcome> UploadAsync(LogWindow window, string agentVersion, byte[] gzip, CancellationToken ct = default);
}
