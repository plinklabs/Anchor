using Anchor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Anchor.Api.Persistence;

/// <summary>
/// Prepares the database at startup where that is the app's job, per
/// environment:
/// <list type="bullet">
/// <item><b>Development</b> — a local SQLite file that does not share the
/// SqlServer migration history, so the schema is built from the current model
/// via <see cref="DatabaseFacade.EnsureCreatedAsync"/> and dev data is seeded.</item>
/// <item><b>Test</b> — the test host owns schema creation (shared in-memory
/// SQLite), so startup does nothing here.</item>
/// <item><b>Production / other</b> — Azure SQL. Startup does not touch the
/// database at all: <c>backend-deploy.yml</c> applies the committed EF Core
/// migrations with a migrations bundle before the new build goes live (#344).
/// Migrating at app start (#205) could race when more than one instance boots,
/// and a failed migration took the API down instead of failing the deploy.</item>
/// </list>
/// The environment branching is the part that has shipped broken before, so it
/// lives behind <see cref="IStartupDatabaseOperations"/> to keep it unit-testable.
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
        // The test host builds its own schema against shared in-memory SQLite.
        if (environment.IsEnvironment("Test"))
        {
            return;
        }

        if (!environment.IsDevelopment())
        {
            // Azure SQL: the deploy pipeline owns the schema. Not even a
            // pending-migrations check here — any query at startup is a
            // database round trip before the API serves its first request.
            logger.LogInformation(
                "Skipping database initialization in {Environment}: the deploy pipeline applies migrations.",
                environment.EnvironmentName);
            return;
        }

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();

        // SQLite dev DB doesn't share migrations with the SqlServer prod
        // schema, so build the schema from the current model instead of
        // running migrations, then seed local dev data.
        await operations.EnsureCreatedAsync(db);
        await operations.SeedDevelopmentDataAsync(db);
    }
}
