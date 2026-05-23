using System.Net;
using System.Net.Http.Json;
using Anchor.Api.Auth;
using Anchor.Api.Controllers;
using Anchor.Api.Realtime;
using Anchor.Domain.Classes;
using Anchor.Domain.Sessions;
using Anchor.Domain.Users;
using Anchor.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Anchor.Api.Tests;

/// <summary>
/// End-to-end coverage for the dev-only REST impersonation path added in #44:
/// a request that carries only the <c>X-Dev-Impersonate-Oid</c> header (no
/// Bearer token) authenticates as the user that header points at, gets the
/// matching role, and goes through the normal controller pipeline. This is
/// what unblocks headless verification (the agent in <c>--inject-token</c>
/// mode, dev scripts, the verify-session-start runner) from needing a real
/// Entra JWT for every API call.
///
/// Uses its own factory variant because the shared
/// <see cref="AnchorApiFactory"/> swaps authentication out for the test-only
/// <c>FakeJwtBearerHandler</c>; here we deliberately want the real
/// <see cref="DevImpersonationAuthHandler"/> registered by Program.cs's
/// IsDevelopment branch to handle the request.
/// </summary>
public sealed class DevImpersonationRestTests : IClassFixture<DevImpersonationRestTests.RealAuthDevFactory>
{
    private readonly RealAuthDevFactory _factory;

    public DevImpersonationRestTests(RealAuthDevFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Post_sessions_with_impersonation_header_authenticates_as_teacher_and_broadcasts()
    {
        var (teacher, @class) = await SeedClassWithTeacherAsync();

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Remove(DevImpersonationAuthHandler.HeaderName);
        client.DefaultRequestHeaders.Add(
            DevImpersonationAuthHandler.HeaderName,
            teacher.EntraOid.ToString());

        var response = await client.PostAsJsonAsync(
            "/sessions",
            new StartSessionRequest(@class.Id, "Strict", null));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<StartSessionResponse>();
        Assert.NotNull(body);
        Assert.Equal(@class.Id, body!.ClassId);

        var recorded = _factory.Services.GetRequiredService<RealAuthDevFactory.RecordingBroadcaster>();
        Assert.Single(recorded.SessionStartedCalls);
        Assert.Equal(body.Id, recorded.SessionStartedCalls[0].Payload.SessionId);
    }

    [Fact]
    public async Task Post_sessions_with_impersonation_header_for_student_is_forbidden_by_teacher_policy()
    {
        var (_, @class) = await SeedClassWithTeacherAsync();
        var student = await SeedUserAsync(UserRole.Student, "Posing Student");

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            DevImpersonationAuthHandler.HeaderName,
            student.EntraOid.ToString());

        var response = await client.PostAsJsonAsync(
            "/sessions",
            new StartSessionRequest(@class.Id, "Strict", null));

        // The handler authenticates the student successfully, but the Teacher
        // authorization policy rejects the request — proving the role claim
        // emitted by DevImpersonationAuthHandler flows through to the policy.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Post_sessions_with_no_auth_at_all_is_unauthorized()
    {
        var (_, @class) = await SeedClassWithTeacherAsync();

        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/sessions",
            new StartSessionRequest(@class.Id, "Strict", null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Post_sessions_with_invalid_impersonation_header_is_unauthorized()
    {
        var (_, @class) = await SeedClassWithTeacherAsync();

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(DevImpersonationAuthHandler.HeaderName, "not-a-guid");

        var response = await client.PostAsJsonAsync(
            "/sessions",
            new StartSessionRequest(@class.Id, "Strict", null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Post_sessions_with_unprovisioned_impersonation_oid_is_unauthorized()
    {
        var (_, @class) = await SeedClassWithTeacherAsync();

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            DevImpersonationAuthHandler.HeaderName,
            Guid.NewGuid().ToString());

        var response = await client.PostAsJsonAsync(
            "/sessions",
            new StartSessionRequest(@class.Id, "Strict", null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<(User teacher, Class @class)> SeedClassWithTeacherAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();

        var teacher = new User
        {
            EntraOid = Guid.NewGuid(),
            DisplayName = "Dev Impersonation Teacher",
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

    private async Task<User> SeedUserAsync(UserRole role, string displayName)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        var user = new User
        {
            EntraOid = Guid.NewGuid(),
            DisplayName = displayName,
            Role = role,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>
    /// Variant of <see cref="AnchorApiFactory"/> that keeps the Development-env
    /// authentication pipeline (so the real <see cref="DevImpersonationAuthHandler"/>
    /// runs) and only swaps the persistence + broadcaster. Cannot reuse
    /// AnchorApiFactory directly because that one swaps in FakeJwtBearerHandler.
    /// </summary>
    public sealed class RealAuthDevFactory : WebApplicationFactory<Program>, IAsyncLifetime
    {
        private readonly SqliteConnection _connection = new("Filename=:memory:");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");

            // Program.cs's Development path requires ConnectionStrings:DefaultConnection
            // before we override the DbContext below.
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = "Data Source=:memory:",
                    // Stub AzureAd so MicrosoftIdentityWeb's option binding succeeds;
                    // we never actually validate a real token in these tests.
                    ["AzureAd:Instance"] = "https://login.microsoftonline.com/",
                    ["AzureAd:TenantId"] = "00000000-0000-0000-0000-000000000000",
                    ["AzureAd:ClientId"] = "00000000-0000-0000-0000-000000000000",
                    ["AzureAd:Audience"] = "api://anchor-test",
                }));

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<AnchorDbContext>>();
                services.RemoveAll<AnchorDbContext>();
                services.AddDbContext<AnchorDbContext>(options =>
                    options.UseSqlite(_connection));

                services.RemoveAll<ISessionBroadcaster>();
                services.AddSingleton<RecordingBroadcaster>();
                services.AddSingleton<ISessionBroadcaster>(sp => sp.GetRequiredService<RecordingBroadcaster>());
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

        /// <summary>Captures broadcasts so tests can assert on payload + recipients.</summary>
        public sealed class RecordingBroadcaster : ISessionBroadcaster
        {
            public List<SessionStartedCall> SessionStartedCalls { get; } = new();
            public List<Guid> SessionEndedCalls { get; } = new();
            public List<BundleUpdatedPayload> BundleUpdatedCalls { get; } = new();

            public Task SessionStartedAsync(
                SessionStartedPayload payload,
                IReadOnlyCollection<Guid> recipientUserIds,
                CancellationToken cancellationToken = default)
            {
                SessionStartedCalls.Add(new SessionStartedCall(payload, recipientUserIds.ToArray()));
                return Task.CompletedTask;
            }

            public Task SessionEndedAsync(Guid sessionId, CancellationToken cancellationToken = default)
            {
                SessionEndedCalls.Add(sessionId);
                return Task.CompletedTask;
            }

            public Task BundleUpdatedAsync(BundleUpdatedPayload payload, CancellationToken cancellationToken = default)
            {
                BundleUpdatedCalls.Add(payload);
                return Task.CompletedTask;
            }
        }

        public sealed record SessionStartedCall(SessionStartedPayload Payload, IReadOnlyList<Guid> RecipientUserIds);
    }
}
