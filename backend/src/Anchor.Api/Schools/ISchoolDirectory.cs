namespace Anchor.Api.Schools;

/// <summary>
/// Reconciles the live set of schools (Entra <c>companyName</c> values from
/// Graph) with the persisted per-school admin state (#301).
///
/// Schools aren't real directory objects — they're a user attribute — so
/// enumerating them from Graph is expensive (no DISTINCT; it pages the whole
/// tenant). They also change rarely (about once a year). So the persisted table
/// is the source of truth for the hot, teacher-facing read, and Graph is only
/// consulted on the rare admin view, to discover newly-appeared schools.
/// </summary>
public interface ISchoolDirectory
{
    /// <summary>
    /// Every known school with its active state, for the admin list. Reconciles
    /// with Graph first — newly-discovered companies are persisted as active —
    /// then returns all rows name-ordered. Touches Graph, so it can throw the
    /// same directory failures as <c>ListCompaniesAsync</c>.
    /// </summary>
    Task<IReadOnlyList<SchoolState>> ListAllAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The persisted school rows only, name-ordered — no Graph call. The
    /// graceful-degradation fallback for the admin list when the directory is
    /// unavailable (#281), so an admin can still see and toggle known schools.
    /// </summary>
    Task<IReadOnlyList<SchoolState>> ListPersistedAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The active school names for the teacher-facing selector. Served straight
    /// from the DB once the feature is configured (no Graph). While the table is
    /// still empty (no school has ever been reconciled), falls back to the live
    /// Graph company list, so teachers keep seeing every school until an admin
    /// first curates — that fallback can throw the usual directory failures.
    /// </summary>
    Task<IReadOnlyList<string>> ListActiveNamesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Persist a school's active state, creating the row if it doesn't exist yet.
    /// Returns the resulting state. Does not touch Graph, so it works even when
    /// the directory is unavailable.
    /// </summary>
    Task<SchoolState> SetActiveAsync(string name, bool isActive, CancellationToken cancellationToken);
}

/// <summary>A school name (Entra company) paired with its active state.</summary>
public sealed record SchoolState(string Name, bool IsActive);
