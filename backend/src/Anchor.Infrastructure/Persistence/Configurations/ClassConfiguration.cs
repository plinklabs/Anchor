using Anchor.Domain.Classes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Anchor.Infrastructure.Persistence.Configurations;

internal sealed class ClassConfiguration : IEntityTypeConfiguration<Class>
{
    public void Configure(EntityTypeBuilder<Class> builder)
    {
        builder.ToTable("Classes");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Name).IsRequired().HasMaxLength(128);
        builder.Property(c => c.SchoolYear).IsRequired().HasMaxLength(16);
        builder.Property(c => c.SchoolTag).HasMaxLength(64);
        builder.Property(c => c.ClassCode).HasMaxLength(32);

        builder.HasIndex(c => new { c.SchoolYear, c.Name }).IsUnique();
        builder.HasIndex(c => new { c.SchoolTag, c.ClassCode });
    }
}
