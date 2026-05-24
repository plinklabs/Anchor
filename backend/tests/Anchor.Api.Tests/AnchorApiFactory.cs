using Anchor.Api.Realtime;
using Anchor.Api.Tests.FakeAuth;
using Anchor.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Anchor.Api.Tests;

public class AnchorApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    // Shared in-memory SQLite: a unique name per factory keeps fixtures
    // hermetic, while `mode=memory&cache=shared` lets every scope open its
    // own SqliteConnection against the same database. The keep-alive
    // connection below is what stops the DB from being torn down when
    // scoped connections close.
    private readonly string _connectionString =
        $"Data Source=file:{Guid.NewGuid():N}?mode=memory&cache=shared";
    private readonly SqliteConnection _keepAlive;

    public AnchorApiFactory()
    {
        _keepAlive = new SqliteConnection(_connectionString);
    }

    protected virtual string EnvironmentName => "Test";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(EnvironmentName);

        // Program.cs's Development path requires ConnectionStrings:DefaultConnection
        // before we override the DbContext below. Supply a harmless placeholder so
        // AddInfrastructureSqlite doesn't throw during host build.
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Data Source=:memory:",
                // The monitor scans tracker state on a timer; tests drive
                // the scan deterministically via HeartbeatMonitor.ScanOnceAsync
                // instead so assertions don't race the timer.
                ["Heartbeat:EnableMonitor"] = "false",
            }));

        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(FakeJwtBearerHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, FakeJwtBearerHandler>(
                    FakeJwtBearerHandler.SchemeName, _ => { });

            services.PostConfigure<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = FakeJwtBearerHandler.SchemeName;
                options.DefaultChallengeScheme = FakeJwtBearerHandler.SchemeName;
            });

            // Drop any DbContext registration Program.cs added on the Development
            // path and point every scope at the shared in-memory database via a
            // fresh connection — passing a connection string (not an instance)
            // lets EF Core open a new SqliteConnection per scope, eliminating
            // the cross-scope contention that flaked SignalR hub tests (#46).
            services.RemoveAll<DbContextOptions<AnchorDbContext>>();
            services.RemoveAll<AnchorDbContext>();
            services.AddDbContext<AnchorDbContext>(options =>
                options.UseSqlite(_connectionString));

            services.RemoveAll<ISessionBroadcaster>();
            services.AddSingleton<RecordingSessionBroadcaster>();
            services.AddSingleton<ISessionBroadcaster>(sp => sp.GetRequiredService<RecordingSessionBroadcaster>());
        });
    }

    public async Task InitializeAsync()
    {
        await _keepAlive.OpenAsync();
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    public new async Task DisposeAsync()
    {
        await _keepAlive.DisposeAsync();
        await base.DisposeAsync();
    }
}
