namespace Anchor.Domain.Schools;

/// <summary>
/// A persisted school (#301). "Schools" are otherwise not an entity — they're the
/// distinct Entra <c>companyName</c> strings fetched live from Graph. This row
/// gives a school somewhere to carry admin-managed state (currently just
/// <see cref="IsActive"/>), keyed by that company string (<see cref="Name"/>).
///
/// The table is the source of truth for the teacher-facing school selector, so
/// it can be served without an expensive Graph enumeration on every read. Rows
/// are reconciled from the live Graph company list when an admin views the
/// Schools tab: newly-discovered companies are inserted as active.
/// </summary>
public sealed class School
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>The Entra <c>companyName</c> this row stands for — the natural key.</summary>
    public required string Name { get; init; }

    /// <summary>Whether teachers see this school in the Classes school selector.</summary>
    public required bool IsActive { get; set; }
}
