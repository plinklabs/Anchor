using Anchor.Domain.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Anchor.Infrastructure.Persistence.Configurations;

internal sealed class SessionEventSummaryConfiguration : IEntityTypeConfiguration<SessionEventSummary>
{
    public void Configure(EntityTypeBuilder<SessionEventSummary> builder)
    {
        builder.ToTable("SessionEventSummaries");
        builder.HasKey(s => new { s.SessionId, s.UserId, s.Kind });

        builder.Property(s => s.Kind).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(s => s.Count).IsRequired();
        builder.Property(s => s.FirstAt).IsRequired();
        builder.Property(s => s.LastAt).IsRequired();

        builder.HasOne(s => s.Session)
            .WithMany()
            .HasForeignKey(s => s.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(s => s.User)
            .WithMany()
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
