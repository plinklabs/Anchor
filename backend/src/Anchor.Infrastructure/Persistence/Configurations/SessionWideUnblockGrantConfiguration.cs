using Anchor.Domain.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Anchor.Infrastructure.Persistence.Configurations;

internal sealed class SessionWideUnblockGrantConfiguration : IEntityTypeConfiguration<SessionWideUnblockGrant>
{
    public void Configure(EntityTypeBuilder<SessionWideUnblockGrant> builder)
    {
        builder.ToTable("SessionWideUnblockGrants");
        // One row per (session, host): a host is either granted class-wide or
        // not. Re-approving the same host is idempotent against this key.
        builder.HasKey(g => new { g.SessionId, g.Host });

        // 253 is the max DNS hostname length (RFC 1035 + the trailing dot
        // convention). Host is required and stored already lowercased by the
        // controller — the column doesn't enforce that, the writer does.
        builder.Property(g => g.Host).HasMaxLength(253).IsRequired();
        builder.Property(g => g.GrantedAt).IsRequired();

        builder.HasOne(g => g.Session)
            .WithMany()
            .HasForeignKey(g => g.SessionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
