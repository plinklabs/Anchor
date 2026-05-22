using Anchor.Api.Realtime;
using Anchor.Api.Tests.FakeAuth;
using Anchor.Domain.Classes;
using Anchor.Domain.Events;
using Anchor.Domain.Sessions;
using Anchor.Domain.Users;
using Anchor.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Anchor.Api.Tests;

public sealed class SessionHubTests : IClassFixture<AnchorApiFactory>
{
    private readonly AnchorApiFactory _factory;

    public SessionHubTests(AnchorApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Unauthenticated_connection_is_rejected()
    {
        await using var connection = BuildConnection(oid: null);

        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());
    }

    [Fact]
    public async Task Join_then_report_event_persists_event_to_db()
    {
        var (student, session) = await SeedSessionWithStudentAsync();

        await using var connection = BuildConnection(student.EntraOid, "Student");
        await connection.StartAsync();

        var joinResult = await connection.InvokeAsync<JoinSessionResult>(
            "JoinSession",
            new JoinSessionRequest(session.Id, JoinCode: null));

        Assert.Equal(session.Id, joinResult.SessionId);
        Assert.Equal(student.Id, joinResult.UserId);

        await connection.InvokeAsync(
            "ReportEvent",
            new ReportEventRequest(
                session.Id,
                Kind: nameof(EventKind.ForegroundChange),
                PayloadJson: "{\"app\":\"chrome\"}",
                OccurredAt: null));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        var @event = await db.Events.AsNoTracking()
            .SingleAsync(e => e.SessionId == session.Id && e.UserId == student.Id);
        Assert.Equal(EventKind.ForegroundChange, @event.Kind);
        Assert.Equal("{\"app\":\"chrome\"}", @event.PayloadJson);
    }

    [Fact]
    public async Task Non_participant_cannot_report_event()
    {
        var (_, session) = await SeedSessionWithStudentAsync();
        var stranger = await SeedUserAsync(UserRole.Student, "Outsider");

        await using var connection = BuildConnection(stranger.EntraOid, "Student");
        await connection.StartAsync();

        var ex = await Assert.ThrowsAsync<HubException>(() =>
            connection.InvokeAsync(
                "ReportEvent",
                new ReportEventRequest(
                    session.Id,
                    nameof(EventKind.ForegroundChange),
                    PayloadJson: "{}",
                    OccurredAt: null)));
        Assert.Contains("Not a participant", ex.Message);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        Assert.False(await db.Events.AnyAsync(e => e.UserId == stranger.Id));
    }

    [Fact]
    public async Task Join_with_wrong_code_is_rejected()
    {
        var (_, session) = await SeedSessionWithStudentAsync();
        var stranger = await SeedUserAsync(UserRole.Student, "Wanderer");

        await using var connection = BuildConnection(stranger.EntraOid, "Student");
        await connection.StartAsync();

        var ex = await Assert.ThrowsAsync<HubException>(() =>
            connection.InvokeAsync<JoinSessionResult>(
                "JoinSession",
                new JoinSessionRequest(session.Id, JoinCode: "WRONG")));
        Assert.Contains("Not a participant", ex.Message);
    }

    [Fact]
    public async Task Join_with_valid_code_creates_participant()
    {
        var (_, session) = await SeedSessionWithStudentAsync();
        var newcomer = await SeedUserAsync(UserRole.Student, "Newcomer");

        await using var connection = BuildConnection(newcomer.EntraOid, "Student");
        await connection.StartAsync();

        var result = await connection.InvokeAsync<JoinSessionResult>(
            "JoinSession",
            new JoinSessionRequest(session.Id, session.JoinCode));

        Assert.Equal(session.Id, result.SessionId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        var participant = await db.SessionParticipants.AsNoTracking()
            .SingleAsync(p => p.SessionId == session.Id && p.UserId == newcomer.Id);
        Assert.NotNull(participant.JoinedAt);
    }

    [Fact]
    public async Task SessionEnded_broadcast_reaches_joined_clients_only()
    {
        var (joiner, session) = await SeedSessionWithStudentAsync();
        var outsider = await SeedUserAsync(UserRole.Student, "Outsider");

        await using var joined = BuildConnection(joiner.EntraOid, "Student");
        await using var outside = BuildConnection(outsider.EntraOid, "Student");
        await joined.StartAsync();
        await outside.StartAsync();

        var joinedSignal = new TaskCompletionSource<Guid>();
        var outsideSignal = new TaskCompletionSource<Guid>();
        joined.On<Guid>(nameof(ISessionHubClient.SessionEnded), id => joinedSignal.TrySetResult(id));
        outside.On<Guid>(nameof(ISessionHubClient.SessionEnded), id => outsideSignal.TrySetResult(id));

        await joined.InvokeAsync<JoinSessionResult>(
            "JoinSession",
            new JoinSessionRequest(session.Id, JoinCode: null));

        var broadcaster = _factory.Services.GetRequiredService<ISessionBroadcaster>();
        await broadcaster.SessionEndedAsync(session.Id);

        var received = await joinedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(session.Id, received);

        var outsiderGotIt = await Task.WhenAny(outsideSignal.Task, Task.Delay(500)) == outsideSignal.Task;
        Assert.False(outsiderGotIt, "Client outside the session group should not receive SessionEnded.");
    }

    private async Task<(User student, Session session)> SeedSessionWithStudentAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();

        var teacher = new User
        {
            EntraOid = Guid.NewGuid(),
            DisplayName = "Test Teacher",
            Role = UserRole.Teacher,
        };
        var student = new User
        {
            EntraOid = Guid.NewGuid(),
            DisplayName = "Test Student",
            Role = UserRole.Student,
        };
        var @class = new Class
        {
            Name = $"Class-{Guid.NewGuid():N}",
            SchoolYear = "2025-2026",
        };
        var membership = new ClassMembership
        {
            ClassId = @class.Id,
            UserId = student.Id,
            Role = ClassMembershipRole.Member,
        };
        var session = new Session
        {
            TeacherId = teacher.Id,
            ClassId = @class.Id,
            Mode = SessionMode.Strict,
            StartedAt = DateTimeOffset.UtcNow,
            JoinCode = $"J{Guid.NewGuid():N}".Substring(0, 8),
        };
        var participant = new SessionParticipant
        {
            SessionId = session.Id,
            UserId = student.Id,
            JoinedAt = null,
        };

        db.Users.AddRange(teacher, student);
        db.Classes.Add(@class);
        db.ClassMemberships.Add(membership);
        db.Sessions.Add(session);
        db.SessionParticipants.Add(participant);
        await db.SaveChangesAsync();

        return (student, session);
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

    private HubConnection BuildConnection(Guid? oid, string? role = null)
    {
        var server = _factory.Server;
        return new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, SessionHub.Path.TrimStart('/')), options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                if (oid is not null)
                {
                    options.Headers[FakeJwtBearerHandler.HeaderOid] = oid.Value.ToString();
                    if (!string.IsNullOrEmpty(role))
                        options.Headers[FakeJwtBearerHandler.HeaderRole] = role;
                    options.Headers[FakeJwtBearerHandler.HeaderName] = "Test User";
                }
            })
            .Build();
    }
}
