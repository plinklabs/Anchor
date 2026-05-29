namespace Anchor.Domain.Classes;

public sealed class Class
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string SchoolYear { get; set; }

    /// Entra `Company` attribute identifying the school within the Arcadia
    /// group (e.g. "SSM"). Nullable for legacy classes seeded before #96; new
    /// classes should set it so roster Graph queries can filter by school.
    public string? SchoolTag { get; set; }

    /// Entra `Department` attribute used as a class code (e.g. "3A"). Class
    /// codes are not unique across the group, which is why they are always
    /// scoped by [SchoolTag] when querying Graph.
    public string? ClassCode { get; set; }

    public ICollection<ClassMembership> Memberships { get; init; } = new List<ClassMembership>();
}
