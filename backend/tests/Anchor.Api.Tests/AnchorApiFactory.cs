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
    private readonly SqliteConnection _connection = new("Filename=:memory:");

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
                // The monitor scans tracker state on a timer; in tests it would
                // race with our shared in-memory SQLite connection. Tests drive
                // the scan directly via HeartbeatMonitor.ScanOnceAsync instead.
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
            // path and swap in our shared in-memory SQLite connection.
            services.RemoveAll<DbContextOptions<AnchorDbContext>>();
            services.RemoveAll<AnchorDbContext>();
            services.AddDbContext<AnchorDbContext>(options =>
                options.UseSqlite(_connection));

            services.RemoveAll<ISessionBroadcaster>();
            services.AddSingleton<RecordingSessionBroadcaster>();
            services.AddSingleton<ISessionBroadcaster>(sp => sp.GetRequiredService<RecordingSessionBroadcaster>());
        });
    }

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    public new async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
        await base.DisposeAsync();
    }
}
