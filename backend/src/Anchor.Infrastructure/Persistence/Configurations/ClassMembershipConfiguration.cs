using Anchor.Domain.Classes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Anchor.Infrastructure.Persistence.Configurations;

internal sealed class ClassMembershipConfiguration : IEntityTypeConfiguration<ClassMembership>
{
    public void Configure(EntityTypeBuilder<ClassMembership> builder)
    {
        builder.ToTable("ClassMemberships");
        builder.HasKey(m => new { m.ClassId, m.UserId });

        builder.Property(m => m.Role).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(m => m.JoinedAt).IsRequired();

        builder.HasOne(m => m.Class)
            .WithMany(c => c.Memberships)
            .HasForeignKey(m => m.ClassId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(m => m.User)
            .WithMany()
            .HasForeignKey(m => m.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(m => m.UserId);
    }
}
