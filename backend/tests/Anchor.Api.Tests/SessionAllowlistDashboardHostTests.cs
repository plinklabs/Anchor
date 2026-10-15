using System.Net;
using System.Net.Http.Json;
using Anchor.Api.Controllers;
using Anchor.Api.Sessions;
using Anchor.Api.Tests.FakeAuth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Anchor.Api.Tests;

/// <summary>
/// End-to-end coverage for #409: the dashboard's host, taken from the
/// deployment's <c>Cors:AllowedOrigins</c>, is in every session's broadcast
/// allowlist, so a session can't lock its own teacher out of the page that
/// controls it. Runs the real host in the non-Development "Test" environment,
/// so it also proves the entry isn't one of the Development-only carve-outs,
/// and that the setting reaches the expander through DI the way App Service
/// supplies it. The expander in isolation is covered by
/// <see cref="SessionAllowlistExpanderTests"/>.
/// </summary>
public sealed class SessionAllowlistDashboardHostTests
    : IClassFixture<SessionAllowlistDashboardHostTests.DashboardOriginFactory>
{
    private const string DashboardOrigin = "https://anchorssm.arcadiascholen.be";

    private readonly DashboardOriginFactory _factory;

    public SessionAllowlistDashboardHostTests(DashboardOriginFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Session_start_broadcast_allows_the_dashboard_host_before_any_bundle_is_picked()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory, studentCount: 1);

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        // No bundles: the dashboard starts every session baseline-only, and the
        // teacher picks bundles afterwards on that same dashboard.
        var response = await client.PostAsJsonAsync("/sessions", new StartSessionRequest(scenario.Class.Id, null));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<StartSessionResponse>();

        var broadcaster = _factory.Services.GetRequiredService<RecordingSessionBroadcaster>();
        var broadcast = Assert.Single(broadcaster.SessionStartedCalls, p => p.SessionId == body!.Id);
        Assert.Contains(
            broadcast.Payload.Domains,
            d => d.MatchType == "Exact" && d.Value == "anchorssm.arcadiascholen.be");
    }

    public sealed class DashboardOriginFactory : AnchorApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            // UseSetting, like App Service's Cors__AllowedOrigins__0: visible to
            // Program.cs's CORS policy as well as to the expander.
            builder.UseSetting($"{SessionAllowlist.DashboardOriginsKey}:0", DashboardOrigin);
        }
    }
}
