using Anchor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Anchor.Api.Persistence;

/// <summary>
/// Brings the database schema up to date at startup, choosing the right
/// strategy per environment:
/// <list type="bullet">
/// <item><b>Development</b> — a local SQLite file that does not share the
/// SqlServer migration history, so the schema is built from the current model
/// via <see cref="DatabaseFacade.EnsureCreatedAsync"/> and dev data is seeded.</item>
/// <item><b>Test</b> — the test host owns schema creation (shared in-memory
/// SQLite), so startup does nothing here.</item>
/// <item><b>Production / other</b> — Azure SQL, where the committed EF Core
/// migrations are applied via <see cref="RelationalDatabaseFacadeExtensions.MigrateAsync"/>
/// so a fresh database gets the full schema and subsequent releases apply new
/// migrations. This is issue #205: previously only the Development branch ran,
/// so production never applied migrations.</item>
/// </list>
/// The environment branching is the part that has shipped broken before, so it
/// lives behind <see cref="IStartupDatabaseOperations"/> to keep it unit-testable
/// without a real SqlServer instance.
/// </summary>
public static class StartupDatabaseInitializer
{
    public static Task InitializeAsync(WebApplication app)
    {
        var operations = app.Services.GetService<IStartupDatabaseOperations>()
            ?? new EfStartupDatabaseOperations();
        var logger = app.Services
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(StartupDatabaseInitializer).FullName!);
        return InitializeAsync(app.Services, app.Environment, operations, logger);
    }

    public static async Task InitializeAsync(
        IServiceProvider services,
        IHostEnvironment environment,
        IStartupDatabaseOperations operations,
        ILogger logger)
    {
        // The test host builds its own schema against shared in-memory SQLite;
        // applying the SqlServer migrations there would fail, so skip entirely.
        if (environment.IsEnvironment("Test"))
        {
            return;
        }

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();

        if (environment.IsDevelopment())
        {
            // SQLite dev DB doesn't share migrations with the SqlServer prod
            // schema, so build the schema from the current model instead of
            // running migrations, then seed local dev data.
            await operations.EnsureCreatedAsync(db);
            await operations.SeedDevelopmentDataAsync(db);
            return;
        }

        // Non-Development (Azure SQL): apply any pending EF Core migrations so a
        // fresh database gets the full schema and releases apply new migrations.
        var pending = (await operations.GetPendingMigrationsAsync(db)).ToList();
        if (pending.Count == 0)
        {
            logger.LogInformation("Database schema is up to date; no migrations to apply.");
        }
        else
        {
            logger.LogInformation(
                "Applying {Count} pending database migration(s): {Migrations}",
                pending.Count,
                string.Join(", ", pending));
        }

        await operations.MigrateAsync(db);

        if (pending.Count > 0)
        {
            logger.LogInformation(
                "Applied {Count} database migration(s): {Migrations}",
                pending.Count,
                string.Join(", ", pending));
        }
    }
}
