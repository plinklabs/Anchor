using Anchor.Domain.Users;

namespace Anchor.Domain.Sessions;

/// <summary>
/// A student's agent log lines from the window around one session (#446),
/// gzipped as the agent sent them: its own log, crash-log entries and the
/// updater's lines that fall inside the window, nothing from outside it. One row
/// per session and student: a later upload for the same pair (the window so far
/// of a running session, then the whole window once it has closed) replaces the
/// earlier one, which only held part of the same lines. Only admins can download
/// one, and the backend deletes it a fixed time after it arrived.
/// </summary>
public sealed class SessionLogUpload
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid SessionId { get; init; }
    public required Guid UserId { get; init; }

    /// <summary>The start of the window the agent cut its lines from.</summary>
    public required DateTimeOffset WindowFrom { get; set; }

    /// <summary>
    /// The end of the window the agent cut its lines to: the window's end, or,
    /// while the window was still open, the moment the agent asked for it.
    /// </summary>
    public required DateTimeOffset WindowTo { get; set; }

    /// <summary>
    /// Whether the upload holds the whole window: the session had ended and its
    /// window had closed when the agent cut its lines. The backend stops asking
    /// for a window once it holds a complete upload of it.
    /// </summary>
    public required bool IsComplete { get; set; }

    public required DateTimeOffset UploadedAt { get; set; }

    /// <summary>The version the uploading agent reported, if any.</summary>
    public string? AgentVersion { get; set; }

    /// <summary>The size of <see cref="Content"/> in bytes, so a listing needn't load it.</summary>
    public required int ContentLength { get; set; }

    /// <summary>The gzipped log text, as the agent sent it.</summary>
    public required byte[] Content { get; set; }

    public Session? Session { get; init; }
    public User? User { get; init; }
}
