using Anchor.Domain.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Anchor.Infrastructure.Persistence.Configurations;

internal sealed class SessionLogUploadConfiguration : IEntityTypeConfiguration<SessionLogUpload>
{
    public void Configure(EntityTypeBuilder<SessionLogUpload> builder)
    {
        builder.ToTable("SessionLogUploads");
        builder.HasKey(u => u.Id);

        builder.Property(u => u.WindowFrom).IsRequired();
        builder.Property(u => u.WindowTo).IsRequired();
        builder.Property(u => u.UploadedAt).IsRequired();
        builder.Property(u => u.AgentVersion).HasMaxLength(64);
        builder.Property(u => u.Content).IsRequired();

        // One upload per session and student (#446): a later one replaces it.
        builder.HasIndex(u => new { u.SessionId, u.UserId }).IsUnique();
        builder.HasIndex(u => u.UserId);

        builder.HasOne(u => u.Session)
            .WithMany()
            .HasForeignKey(u => u.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(u => u.User)
            .WithMany()
            .HasForeignKey(u => u.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
