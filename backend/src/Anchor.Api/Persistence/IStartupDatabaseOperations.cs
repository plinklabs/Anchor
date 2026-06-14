using Anchor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Anchor.Api.Persistence;

/// <summary>
/// The database operations <see cref="StartupDatabaseInitializer"/> performs,
/// behind an interface so the per-environment branching can be unit-tested
/// without a real SqlServer instance (the SqlServer migrations cannot run on
/// the SQLite providers used in dev/test).
/// </summary>
public interface IStartupDatabaseOperations
{
    /// <summary>Builds the schema from the current model (Development/SQLite).</summary>
    Task EnsureCreatedAsync(AnchorDbContext db);

    /// <summary>Seeds local development data after the schema is created.</summary>
    Task SeedDevelopmentDataAsync(AnchorDbContext db);

    /// <summary>Migrations present in the assembly but not yet applied to the database.</summary>
    Task<IEnumerable<string>> GetPendingMigrationsAsync(AnchorDbContext db);

    /// <summary>Applies all pending EF Core migrations (non-Development/Azure SQL).</summary>
    Task MigrateAsync(AnchorDbContext db);
}

/// <summary>
/// Default implementation that delegates to EF Core's relational
/// <see cref="DatabaseFacade"/> and the infrastructure dev-data seeder.
/// </summary>
public sealed class EfStartupDatabaseOperations : IStartupDatabaseOperations
{
    public Task EnsureCreatedAsync(AnchorDbContext db) => db.Database.EnsureCreatedAsync();

    public Task SeedDevelopmentDataAsync(AnchorDbContext db) => DevDataSeeder.SeedAsync(db);

    public async Task<IEnumerable<string>> GetPendingMigrationsAsync(AnchorDbContext db)
        => await db.Database.GetPendingMigrationsAsync();

    public Task MigrateAsync(AnchorDbContext db) => db.Database.MigrateAsync();
}
