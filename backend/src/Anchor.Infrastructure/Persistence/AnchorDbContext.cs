using Anchor.Domain.Bundles;
using Anchor.Domain.Classes;
using Anchor.Domain.Events;
using Anchor.Domain.Sessions;
using Anchor.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace Anchor.Infrastructure.Persistence;

public sealed class AnchorDbContext : DbContext
{
    public AnchorDbContext(DbContextOptions<AnchorDbContext> options) : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<Class> Classes => Set<Class>();
    public DbSet<ClassMembership> ClassMemberships => Set<ClassMembership>();
    public DbSet<Bundle> Bundles => Set<Bundle>();
    public DbSet<BundleEntry> BundleEntries => Set<BundleEntry>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<SessionBundle> SessionBundles => Set<SessionBundle>();
    public DbSet<SessionParticipant> SessionParticipants => Set<SessionParticipant>();
    public DbSet<SessionUnblockGrant> SessionUnblockGrants => Set<SessionUnblockGrant>();
    public DbSet<SessionWideUnblockGrant> SessionWideUnblockGrants => Set<SessionWideUnblockGrant>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<SessionEventSummary> SessionEventSummaries => Set<SessionEventSummary>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AnchorDbContext).Assembly);
    }
}
