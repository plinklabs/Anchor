using Anchor.Api.Realtime;
using Anchor.Api.Tests.FakeAuth;
using Anchor.Domain.Users;
using Anchor.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Anchor.Api.Tests;

/// <summary>
/// Exercises the dev-only <c>X-Dev-Impersonate-Oid</c> header path on
/// <see cref="SessionHub"/>. The default <see cref="AnchorApiFactory"/> runs
/// under environment <c>Test</c>, in which the header is intentionally
/// ignored; this fixture flips the host to <c>Development</c> so the
/// impersonation branch is exercised end-to-end.
/// </summary>
public sealed class DevImpersonationHubTests : IClassFixture<DevImpersonationHubTests.DevAnchorApiFactory>
{
    private static readonly Guid SeededStudentOid = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly DevAnchorApiFactory _factory;

    public DevImpersonationHubTests(DevAnchorApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Connection_with_impersonation_header_receives_broadcast_targeted_at_impersonated_user()
    {
        var seededStudent = await ResolveSeededStudentAsync();

        // The signed-in token belongs to an unrelated random user; the header
        // overrides resolution to the seeded student. This mirrors the real
        // dev-laptop setup: signed in as a teacher, agent impersonates a student.
        var tokenOid = Guid.NewGuid();
        await using var connection = BuildConnection(
            tokenOid,
            "Student",
            impersonateOid: SeededStudentOid);

        var received = new TaskCompletionSource<SessionStartedPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<SessionStartedPayload>(
            nameof(ISessionHubClient.SessionStarted),
            payload => received.TrySetResult(payload));

        await connection.StartAsync();

        var payload = NewPayload();
        var broadcaster = _factory.Services.GetRequiredService<ISessionBroadcaster>();
        await broadcaster.SessionStartedAsync(payload, new[] { seededStudent.Id });

        var got = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(payload.SessionId, got.SessionId);
        Assert.Equal(payload.ClassId, got.ClassId);
        Assert.Equal(payload.Mode, got.Mode);
        Assert.Equal(payload.JoinCode, got.JoinCode);
    }

    [Fact]
    public async Task Connection_for_a_different_user_does_not_receive_other_users_broadcast()
    {
        var seededStudent = await ResolveSeededStudentAsync();

        // Connect a real provisioned user that is NOT the seeded student.
        // A broadcast targeted at the seeded student must not leak to this
        // connection. With #44 the dev impersonation header is also accepted
        // by the hub via DevImpersonationAuthHandler, so we authenticate via
        // the impersonation header pointed at this user.
        var otherUser = await SeedUserAsync(UserRole.Student, "Other User");
        await using var connection = BuildConnection(otherUser.EntraOid, "Student", impersonateOid: otherUser.EntraOid);

        var leaked = new TaskCompletionSource<SessionStartedPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<SessionStartedPayload>(
            nameof(ISessionHubClient.SessionStarted),
            payload => leaked.TrySetResult(payload));

        await connection.StartAsync();

        var broadcaster = _factory.Services.GetRequiredService<ISessionBroadcaster>();
        await broadcaster.SessionStartedAsync(NewPayload(), new[] { seededStudent.Id });

        var raced = await Task.WhenAny(leaked.Task, Task.Delay(500));
        Assert.NotSame(leaked.Task, raced);
    }

    private async Task<User> ResolveSeededStudentAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        return await db.Users.AsNoTracking().SingleAsync(u => u.EntraOid == SeededStudentOid);
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

    private HubConnection BuildConnection(Guid oid, string role, Guid? impersonateOid)
    {
        var server = _factory.Server;
        return new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, SessionHub.Path.TrimStart('/')), options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.Headers[FakeJwtBearerHandler.HeaderOid] = oid.ToString();
                options.Headers[FakeJwtBearerHandler.HeaderRole] = role;
                options.Headers[FakeJwtBearerHandler.HeaderName] = "Token User";
                if (impersonateOid is Guid impersonate)
                    options.Headers[SessionHub.DevImpersonateOidHeader] = impersonate.ToString();
            })
            .Build();
    }

    private static SessionStartedPayload NewPayload() => new(
        SessionId: Guid.NewGuid(),
        ClassId: Guid.NewGuid(),
        Mode: "Strict",
        StartedAt: DateTimeOffset.UtcNow,
        JoinCode: "654321",
        Apps: Array.Empty<AllowedAppDto>(),
        Domains: Array.Empty<AllowedDomainDto>());

    public sealed class DevAnchorApiFactory : AnchorApiFactory
    {
        protected override string EnvironmentName => "Development";
    }
}
