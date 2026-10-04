using Anchor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Anchor.Api.Persistence;

/// <summary>
/// The database operations <see cref="StartupDatabaseInitializer"/> performs,
/// behind an interface so the per-environment branching can be unit-tested
/// without touching a real database. Only Development has any: production
/// startup leaves the database alone, because the deploy pipeline applies the
/// migrations (#344).
/// </summary>
public interface IStartupDatabaseOperations
{
    /// <summary>Builds the schema from the current model (Development/SQLite).</summary>
    Task EnsureCreatedAsync(AnchorDbContext db);

    /// <summary>Seeds local development data after the schema is created.</summary>
    Task SeedDevelopmentDataAsync(AnchorDbContext db);
}

/// <summary>
/// Default implementation that delegates to EF Core's
/// <see cref="DatabaseFacade"/> and the infrastructure dev-data seeder.
/// </summary>
public sealed class EfStartupDatabaseOperations : IStartupDatabaseOperations
{
    public Task EnsureCreatedAsync(AnchorDbContext db) => db.Database.EnsureCreatedAsync();

    public Task SeedDevelopmentDataAsync(AnchorDbContext db) => DevDataSeeder.SeedAsync(db);
}
