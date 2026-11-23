using Anchor.Domain.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Anchor.Infrastructure.Persistence.Configurations;

internal sealed class LogAccessConfiguration : IEntityTypeConfiguration<LogAccess>
{
    public void Configure(EntityTypeBuilder<LogAccess> builder)
    {
        builder.ToTable("LogAccesses");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.AccessedAt).IsRequired();
        builder.Property(a => a.AdminDisplayName).IsRequired().HasMaxLength(256);
        builder.Property(a => a.SessionStartedAt).IsRequired();
        builder.Property(a => a.ClassName).IsRequired().HasMaxLength(128);
        builder.Property(a => a.StudentDisplayName).IsRequired().HasMaxLength(256);
        builder.Property(a => a.Purpose).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(a => a.Reason).IsRequired().HasMaxLength(LogAccess.MaxReasonLength);
        builder.Property(a => a.RequestedBy).HasMaxLength(LogAccess.MaxRequestedByLength);

        // No foreign keys (#566): a record outlives the upload it is about, and
        // may outlive the session and the users, whose names it keeps.
        builder.HasIndex(a => a.AccessedAt);
        builder.HasIndex(a => new { a.SessionId, a.StudentUserId });
    }
}
