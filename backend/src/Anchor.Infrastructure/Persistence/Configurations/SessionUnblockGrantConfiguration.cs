using Anchor.Domain.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Anchor.Infrastructure.Persistence.Configurations;

internal sealed class SessionUnblockGrantConfiguration : IEntityTypeConfiguration<SessionUnblockGrant>
{
    public void Configure(EntityTypeBuilder<SessionUnblockGrant> builder)
    {
        builder.ToTable("SessionUnblockGrants");
        builder.HasKey(g => new { g.SessionId, g.UserId, g.Host });

        // 253 is the max DNS hostname length (RFC 1035 + the trailing dot
        // convention). Host is required and stored already lowercased by the
        // controller — the column doesn't enforce that, the writer does.
        builder.Property(g => g.Host).HasMaxLength(253).IsRequired();
        builder.Property(g => g.GrantedAt).IsRequired();

        builder.HasOne(g => g.Session)
            .WithMany()
            .HasForeignKey(g => g.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(g => g.User)
            .WithMany()
            .HasForeignKey(g => g.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(g => new { g.SessionId, g.UserId });
    }
}
