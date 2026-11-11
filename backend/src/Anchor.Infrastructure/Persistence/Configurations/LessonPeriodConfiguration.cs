using Anchor.Domain.Schools;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Anchor.Infrastructure.Persistence.Configurations;

internal sealed class LessonPeriodConfiguration : IEntityTypeConfiguration<LessonPeriod>
{
    public void Configure(EntityTypeBuilder<LessonPeriod> builder)
    {
        builder.ToTable("LessonPeriods");
        builder.HasKey(p => p.Id);

        // School-local times of day (#447), not instants: they're turned into
        // an instant on the day they're used, in the school's time zone, so a
        // period keeps its wall-clock time across a DST change.
        builder.Property(p => p.Start).IsRequired();
        builder.Property(p => p.End).IsRequired();

        builder.HasIndex(p => p.SchoolId);
    }
}
