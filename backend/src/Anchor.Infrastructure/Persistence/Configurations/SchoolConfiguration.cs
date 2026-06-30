using Anchor.Domain.Schools;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Anchor.Infrastructure.Persistence.Configurations;

internal sealed class SchoolConfiguration : IEntityTypeConfiguration<School>
{
    public void Configure(EntityTypeBuilder<School> builder)
    {
        builder.ToTable("Schools");
        builder.HasKey(s => s.Id);

        // The Entra companyName is the school's identity; one row per company.
        builder.Property(s => s.Name).IsRequired().HasMaxLength(256);
        builder.HasIndex(s => s.Name).IsUnique();

        builder.Property(s => s.IsActive).IsRequired();
    }
}
