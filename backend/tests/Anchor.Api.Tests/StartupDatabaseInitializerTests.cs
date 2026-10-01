using Anchor.Api.Persistence;
using Anchor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Anchor.Api.Tests;

/// <summary>
/// Development startup builds the SQLite schema (EnsureCreated) and seeds dev
/// data; Test does nothing; Production and every other environment leave the
/// database alone, because the deploy pipeline applies migrations (#344,
/// replacing the startup migrations of #205). The branching is exercised
/// through a fake operations implementation so no real database is involved.
/// </summary>
public sealed class StartupDatabaseInitializerTests
{
    private sealed class FakeStartupDatabaseOperations : IStartupDatabaseOperations
    {
        public int EnsureCreatedCalls { get; private set; }
        public int SeedCalls { get; private set; }

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
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Anchor.Api.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static ServiceProvider BuildServices(bool withDbContext)
    {
        var services = new ServiceCollection();
        if (withDbContext)
        {
            // A real (SQLite) AnchorDbContext so CreateScope/GetRequiredService
            // resolves; the fake operations never touch its schema.
            services.AddDbContext<AnchorDbContext>(o => o.UseSqlite("Data Source=:memory:"));
        }
        return services.BuildServiceProvider();
    }

    private static async Task RunAsync(
        string environment, FakeStartupDatabaseOperations ops, bool withDbContext = true)
    {
        await using var provider = BuildServices(withDbContext);
        var env = new FakeHostEnvironment { EnvironmentName = environment };
        await StartupDatabaseInitializer.InitializeAsync(
            provider, env, ops, NullLogger.Instance);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task NonDevelopment_DoesNotTouchTheDatabase(string environment)
    {
        var ops = new FakeStartupDatabaseOperations();

        // No AnchorDbContext registered: resolving one would throw, so this
        // also proves startup doesn't create a context at all.
        await RunAsync(environment, ops, withDbContext: false);

        Assert.Equal(0, ops.EnsureCreatedCalls);
        Assert.Equal(0, ops.SeedCalls);
    }

    [Fact]
    public async Task Development_EnsureCreatedAndSeeds()
    {
        var ops = new FakeStartupDatabaseOperations();

        await RunAsync(Environments.Development, ops);

        Assert.Equal(1, ops.EnsureCreatedCalls);
        Assert.Equal(1, ops.SeedCalls);
    }

    [Fact]
    public async Task Test_DoesNothing()
    {
        var ops = new FakeStartupDatabaseOperations();

        await RunAsync("Test", ops, withDbContext: false);

        Assert.Equal(0, ops.EnsureCreatedCalls);
        Assert.Equal(0, ops.SeedCalls);
    }
}
