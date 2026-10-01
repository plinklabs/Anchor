using System.Data.Common;
using System.Net;
using Anchor.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Anchor.Api.Tests;

/// <summary>
/// Boots the real <c>Program</c> the way App Service runs it — Production
/// environment, the SqlServer provider, the real hosted services — and checks
/// that startup never opens a database connection (#344). The deploy pipeline
/// applies migrations, <see cref="Events.EventPruner"/> waits for its
/// scheduled hour, and <see cref="Sessions.SessionAutoEnder"/> waits a sweep
/// interval before looking for forgotten sessions (#345), so the first request
/// is served without a database round trip. Every connection attempt is
/// recorded and failed by an EF interceptor, so nothing dials out.
/// </summary>
public sealed class ProductionStartupTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Production_host_starts_and_serves_requests_without_touching_the_database()
    {
        // 10:00 UTC; the pruner's first run is at 02:00 UTC, 16 h away.
        var clock = new TimerRecordingTimeProvider(new DateTimeOffset(2026, 3, 2, 10, 0, 0, TimeSpan.Zero));
        var probe = new DatabaseContactProbe();
        await using var factory = new ProductionFactory(clock, probe);

        // Runs Program.cs (including StartupDatabaseInitializer) and starts the
        // hosted services; an attempt to migrate would fail the host here.
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // The pruner has parked on its wait for 02:00 without a prune first, and
        // the auto-end on its first sweep interval without a sweep first.
        await clock.WaitForTimerAsync(due => due == TimeSpan.FromHours(16), Timeout);
        await clock.WaitForTimerAsync(due => due == TimeSpan.FromMinutes(5), Timeout);
        Assert.Equal(0, probe.ConnectionAttempts);

        // The scheduled runs are the first things that reach for the database.
        clock.Advance(TimeSpan.FromHours(16));
        await probe.FirstAttempt.WaitAsync(Timeout);
    }

    private sealed class ProductionFactory : WebApplicationFactory<Program>
    {
        private readonly TimeProvider _clock;
        private readonly DatabaseContactProbe _probe;

        public ProductionFactory(TimeProvider clock, DatabaseContactProbe probe)
        {
            _clock = clock;
            _probe = probe;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Production);

            // UseSetting, not ConfigureAppConfiguration: Program.cs reads the
            // connection string before Build(), and only host settings are
            // visible that early under minimal hosting.
            // The App Service connection string's shape. The probe fails every
            // open before SqlClient would resolve this host.
            builder.UseSetting(
                "ConnectionStrings:DefaultConnection",
                "Server=tcp:anchor-startup-probe.invalid,1433;Database=anchordb;User ID=anchoradmin;Password=unused;Encrypt=true;");
            // Placeholder Entra config so Microsoft.Identity.Web's options
            // bind; no token is validated here.
            builder.UseSetting("AzureAd:Instance", "https://login.microsoftonline.com/");
            builder.UseSetting("AzureAd:TenantId", "00000000-0000-0000-0000-000000000000");
            builder.UseSetting("AzureAd:ClientId", "00000000-0000-0000-0000-000000000000");
            builder.UseSetting("AzureAd:Audience", "api://anchor-test");

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(_clock);
                // On top of Program.cs's UseSqlServer registration, not instead of it.
                services.ConfigureDbContext<AnchorDbContext>(o => o.AddInterceptors(_probe));
            });
        }
    }

    private sealed class DatabaseContactProbe : DbConnectionInterceptor
    {
        private readonly TaskCompletionSource _firstAttempt =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _attempts;

        public int ConnectionAttempts => Volatile.Read(ref _attempts);

        public Task FirstAttempt => _firstAttempt.Task;

        public override InterceptionResult ConnectionOpening(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
            => throw Record();

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default)
            => throw Record();

        private InvalidOperationException Record()
        {
            Interlocked.Increment(ref _attempts);
            _firstAttempt.TrySetResult();
            return new InvalidOperationException("ProductionStartupTests: the database is unreachable.");
        }
    }
}
