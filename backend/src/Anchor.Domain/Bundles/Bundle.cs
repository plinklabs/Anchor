namespace Anchor.Domain.Bundles;

public sealed class Bundle
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required int Version { get; set; }

    /// <summary>
    /// Soft-delete flag (#75). Archived bundles disappear from the picker and
    /// from the catalogue listing, but their rows stay so historical sessions
    /// (SessionBundle references) still resolve when viewed later.
    /// </summary>
    public bool IsArchived { get; set; }

    public ICollection<BundleEntry> Entries { get; init; } = new List<BundleEntry>();
}
