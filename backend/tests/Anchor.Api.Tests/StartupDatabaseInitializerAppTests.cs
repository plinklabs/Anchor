using Anchor.Api.Persistence;
using Anchor.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Anchor.Api.Tests;

/// <summary>
/// Exercises the <see cref="WebApplication"/>-bound overload of
/// <see cref="StartupDatabaseInitializer.InitializeAsync(WebApplication)"/> —
/// the exact call <c>Program.cs</c> makes at startup — so the glue that reads
/// the environment, resolves <see cref="IStartupDatabaseOperations"/> and builds
/// the logger is covered, not just the inner branching. A fake operations
/// implementation stands in for EF so no SqlServer is required.
/// </summary>
public sealed class StartupDatabaseInitializerAppTests
{
    private sealed class RecordingOperations : IStartupDatabaseOperations
    {
        public int EnsureCreatedCalls { get; private set; }
        public int MigrateCalls { get; private set; }

        public Task EnsureCreatedAsync(AnchorDbContext db)
        {
            EnsureCreatedCalls++;
            return Task.CompletedTask;
        }

        public Task SeedDevelopmentDataAsync(AnchorDbContext db) => Task.CompletedTask;

        public Task<IEnumerable<string>> GetPendingMigrationsAsync(AnchorDbContext db)
            => Task.FromResult<IEnumerable<string>>(new[] { "20260612181911_SessionWideUnblockGrants" });

        public Task MigrateAsync(AnchorDbContext db)
        {
            MigrateCalls++;
            return Task.CompletedTask;
        }
    }

    private static WebApplication BuildApp(string environment, RecordingOperations ops)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment,
        });
        builder.Services.AddDbContext<AnchorDbContext>(o => o.UseSqlite("Data Source=:memory:"));
        builder.Services.AddSingleton<IStartupDatabaseOperations>(ops);
        return builder.Build();
    }

    [Fact]
    public async Task ProductionApp_AppliesMigrationsViaInjectedOperations()
    {
        var ops = new RecordingOperations();
        await using var app = BuildApp(Environments.Production, ops);

        await StartupDatabaseInitializer.InitializeAsync(app);

        Assert.Equal(1, ops.MigrateCalls);
        Assert.Equal(0, ops.EnsureCreatedCalls);
    }

    [Fact]
    public async Task DevelopmentApp_EnsureCreatedViaInjectedOperations()
    {
        var ops = new RecordingOperations();
        await using var app = BuildApp(Environments.Development, ops);

        await StartupDatabaseInitializer.InitializeAsync(app);

        Assert.Equal(1, ops.EnsureCreatedCalls);
        Assert.Equal(0, ops.MigrateCalls);
    }
}
