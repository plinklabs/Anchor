using Anchor.Api.Persistence;
using Anchor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Anchor.Api.Tests;

/// <summary>
/// Guards issue #205: on non-Development startup the API must apply EF Core
/// migrations (Azure SQL), while Development keeps EnsureCreated + dev seed and
/// Test does nothing. The branching is exercised through a fake operations
/// implementation so it runs without a real SqlServer instance (the committed
/// SqlServer migrations can't run on the SQLite providers used here).
/// </summary>
public sealed class StartupDatabaseInitializerTests
{
    private sealed class FakeStartupDatabaseOperations : IStartupDatabaseOperations
    {
        public int EnsureCreatedCalls { get; private set; }
        public int SeedCalls { get; private set; }
        public int MigrateCalls { get; private set; }
        public int GetPendingCalls { get; private set; }
        public IReadOnlyList<string> PendingMigrations { get; set; } = Array.Empty<string>();

        public Task EnsureCreatedAsync(AnchorDbContext db)
        {
            EnsureCreatedCalls++;
            return Task.CompletedTask;
        }

        public Task SeedDevelopmentDataAsync(AnchorDbContext db)
        {
            SeedCalls++;
            return Task.CompletedTask;
        }

        public Task<IEnumerable<string>> GetPendingMigrationsAsync(AnchorDbContext db)
        {
            GetPendingCalls++;
            return Task.FromResult<IEnumerable<string>>(PendingMigrations);
        }

        public Task MigrateAsync(AnchorDbContext db)
        {
            MigrateCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Anchor.Api.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        // A real (SQLite) AnchorDbContext so CreateScope/GetRequiredService
        // resolves; the fake operations never touch its schema.
        services.AddDbContext<AnchorDbContext>(o => o.UseSqlite("Data Source=:memory:"));
        return services.BuildServiceProvider();
    }

    private static async Task RunAsync(string environment, FakeStartupDatabaseOperations ops)
    {
        await using var provider = BuildServices();
        var env = new FakeHostEnvironment { EnvironmentName = environment };
        await StartupDatabaseInitializer.InitializeAsync(
            provider, env, ops, NullLogger.Instance);
    }

    [Fact]
    public async Task Production_AppliesMigrations_AndDoesNotEnsureCreated()
    {
        var ops = new FakeStartupDatabaseOperations
        {
            PendingMigrations = new[] { "20260522203145_InitialCreate" },
        };

        await RunAsync(Environments.Production, ops);

        Assert.Equal(1, ops.MigrateCalls);
        Assert.Equal(0, ops.EnsureCreatedCalls);
        Assert.Equal(0, ops.SeedCalls);
    }

    [Fact]
    public async Task NonDevelopmentCustomEnvironment_AppliesMigrations()
    {
        var ops = new FakeStartupDatabaseOperations();

        await RunAsync("Staging", ops);

        Assert.Equal(1, ops.MigrateCalls);
        Assert.Equal(0, ops.EnsureCreatedCalls);
    }

    [Fact]
    public async Task Development_EnsureCreatedAndSeeds_WithoutMigrating()
    {
        var ops = new FakeStartupDatabaseOperations();

        await RunAsync(Environments.Development, ops);

        Assert.Equal(1, ops.EnsureCreatedCalls);
        Assert.Equal(1, ops.SeedCalls);
        Assert.Equal(0, ops.MigrateCalls);
    }

    [Fact]
    public async Task Test_DoesNothing()
    {
        var ops = new FakeStartupDatabaseOperations();

        await RunAsync("Test", ops);

        Assert.Equal(0, ops.EnsureCreatedCalls);
        Assert.Equal(0, ops.SeedCalls);
        Assert.Equal(0, ops.MigrateCalls);
        Assert.Equal(0, ops.GetPendingCalls);
    }

    [Fact]
    public async Task Production_NoPendingMigrations_StillCallsMigrate()
    {
        // MigrateAsync is a no-op when nothing is pending; calling it
        // unconditionally keeps the "fresh DB gets full schema" guarantee.
        var ops = new FakeStartupDatabaseOperations
        {
            PendingMigrations = Array.Empty<string>(),
        };

        await RunAsync(Environments.Production, ops);

        Assert.Equal(1, ops.MigrateCalls);
        Assert.Equal(1, ops.GetPendingCalls);
    }
}
