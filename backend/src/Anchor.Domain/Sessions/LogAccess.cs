namespace Anchor.Domain.Sessions;

/// <summary>
/// The record of one admin's download of one student's log upload of one
/// session (#566): who, when, why and, for a fraud suspicion, at whose request.
/// Written before the log leaves the backend, in the same transaction, so no
/// log is downloaded without one. Append-only: nothing in the API changes or
/// deletes one; the backend prunes them
/// <c>LogAccess:RetentionDays</c> (a year) after the download, long after the
/// logs themselves are gone. That is also why a record holds the names and the
/// session's class and start as they were, and no foreign keys: it outlives
/// the upload, and may outlive the session and the users.
/// </summary>
public sealed class LogAccess
{
    /// <summary>A reason shorter than this says nothing a reviewer can check.</summary>
    public const int MinReasonLength = 10;

    public const int MaxReasonLength = 1000;

    public const int MaxRequestedByLength = 200;

    public Guid Id { get; init; } = Guid.NewGuid();

    public required DateTimeOffset AccessedAt { get; init; }

    /// <summary>The admin who downloaded the log.</summary>
    public required Guid AdminUserId { get; init; }

    public required string AdminDisplayName { get; init; }

    public required Guid SessionId { get; init; }

    public required DateTimeOffset SessionStartedAt { get; init; }

    /// <summary>The session's class, as it was named at the download.</summary>
    public required string ClassName { get; init; }

    /// <summary>The student whose log was downloaded.</summary>
    public required Guid StudentUserId { get; init; }

    public required string StudentDisplayName { get; init; }

    public required LogAccessPurpose Purpose { get; init; }

    /// <summary>Why the admin needed the log, in their own words.</summary>
    public required string Reason { get; init; }

    /// <summary>
    /// Who in the school's leadership asked for it: required for a
    /// <see cref="LogAccessPurpose.FraudSuspicion"/>, else whoever the admin
    /// named, if anyone.
    /// </summary>
    public string? RequestedBy { get; init; }
}
