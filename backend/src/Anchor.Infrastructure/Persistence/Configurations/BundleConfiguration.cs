using Anchor.Domain.Bundles;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Anchor.Infrastructure.Persistence.Configurations;

internal sealed class BundleConfiguration : IEntityTypeConfiguration<Bundle>
{
    public void Configure(EntityTypeBuilder<Bundle> builder)
    {
        builder.ToTable("Bundles");
        builder.HasKey(b => b.Id);

        builder.Property(b => b.Name).IsRequired().HasMaxLength(128);
        builder.Property(b => b.Version).IsRequired();
        builder.Property(b => b.IsArchived).IsRequired().HasDefaultValue(false);

        builder.HasIndex(b => new { b.Name, b.Version }).IsUnique();
    }
}
