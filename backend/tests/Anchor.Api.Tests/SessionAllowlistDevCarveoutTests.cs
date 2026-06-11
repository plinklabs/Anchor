using System.Net;
using System.Net.Http.Json;
using Anchor.Api.Auth;
using Anchor.Api.Controllers;
using Anchor.Domain.Classes;
using Anchor.Domain.Users;
using Anchor.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Anchor.Api.Tests;

/// <summary>
/// End-to-end coverage for the dev-only allowlist carve-outs (#125): when the
/// backend runs in a Development build, every session's broadcast allowlist
/// must always include localhost/127.0.0.1 (so the dashboard + backend stay
/// reachable to stop the session) and VS Code (so the agent doesn't lock the
/// developer out of their editor).
///
/// This goes through the real Development host — reusing
/// <see cref="DevImpersonationRestTests.RealAuthDevFactory"/>, which boots
/// Program.cs under <c>Development</c> — so it proves the
/// <c>IHostEnvironment.IsDevelopment()</c> gate is actually wired into the
/// expander via DI and reaches the broadcast, not just the expander in
/// isolation (that's covered by <see cref="SessionAllowlistExpanderTests"/>).
/// Each test class gets its own class-fixture instance, so the recording
/// broadcaster here doesn't share state with DevImpersonationRestTests.
/// </summary>
public sealed class SessionAllowlistDevCarveoutTests
    : IClassFixture<DevImpersonationRestTests.RealAuthDevFactory>
{
    private readonly DevImpersonationRestTests.RealAuthDevFactory _factory;

    public SessionAllowlistDevCarveoutTests(DevImpersonationRestTests.RealAuthDevFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Development_session_broadcast_includes_localhost_and_vscode()
    {
        var (teacher, @class) = await SeedClassWithTeacherAsync();

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            DevImpersonationAuthHandler.HeaderName,
            teacher.EntraOid.ToString());

        var response = await client.PostAsJsonAsync(
            "/sessions",
            new StartSessionRequest(@class.Id, null));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<StartSessionResponse>();
        Assert.NotNull(body);

        var recorded = _factory.Services
            .GetRequiredService<DevImpersonationRestTests.RealAuthDevFactory.RecordingBroadcaster>();
        var payload = recorded.SessionStartedCalls
            .Single(c => c.Payload.SessionId == body!.Id)
            .Payload;

        Assert.Contains(payload.Domains, d => d.MatchType == "Exact" && d.Value == "localhost");
        Assert.Contains(payload.Domains, d => d.MatchType == "Exact" && d.Value == "127.0.0.1");
        Assert.Contains(payload.Apps, a => a.MatchKind == "ProcessName" && a.Value == "Code");
        // Baseline still rides along — the carve-outs are additive.
        Assert.Contains(payload.Apps, a => a.MatchKind == "ProcessName" && a.Value == "msedge");
    }

    private async Task<(User teacher, Class @class)> SeedClassWithTeacherAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();

        var teacher = new User
        {
            EntraOid = Guid.NewGuid(),
            DisplayName = "Carveout Teacher",
            Role = UserRole.Teacher,
        };
        var @class = new Class
        {
            Name = $"Class-{Guid.NewGuid():N}",
            SchoolYear = "2025-2026",
        };
        var membership = new ClassMembership
        {
            ClassId = @class.Id,
            UserId = teacher.Id,
            Role = ClassMembershipRole.Teacher,
        };

        db.Users.Add(teacher);
        db.Classes.Add(@class);
        db.ClassMemberships.Add(membership);
        await db.SaveChangesAsync();
        return (teacher, @class);
    }
}
