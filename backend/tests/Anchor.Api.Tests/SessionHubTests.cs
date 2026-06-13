using System.Net.Http.Json;
using Anchor.Api.Controllers;
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
    public async Task Decline_for_roster_member_records_event_and_sets_declined_at()
    {
        var (student, session) = await SeedSessionWithStudentAsync();

        await using var connection = BuildConnection(student.EntraOid, "Student");
        await connection.StartAsync();

        await connection.InvokeAsync(
            "DeclineSession",
            new DeclineSessionRequest(session.Id, Reason: "user_cancelled"));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();

        var participant = await db.SessionParticipants.AsNoTracking()
            .SingleAsync(p => p.SessionId == session.Id && p.UserId == student.Id);
        Assert.NotNull(participant.DeclinedAt);
        Assert.Null(participant.JoinedAt);

        var @event = await db.Events.AsNoTracking()
            .SingleAsync(e => e.SessionId == session.Id && e.UserId == student.Id);
        Assert.Equal(EventKind.JoinDeclined, @event.Kind);
        Assert.Contains("user_cancelled", @event.PayloadJson);
    }

    [Fact]
    public async Task Decline_for_non_roster_user_creates_declined_participant_row()
    {
        var (_, session) = await SeedSessionWithStudentAsync();
        var stranger = await SeedUserAsync(UserRole.Student, "Outsider");

        await using var connection = BuildConnection(stranger.EntraOid, "Student");
        await connection.StartAsync();

        await connection.InvokeAsync(
            "DeclineSession",
            new DeclineSessionRequest(session.Id, Reason: null));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();

        var participant = await db.SessionParticipants.AsNoTracking()
            .SingleAsync(p => p.SessionId == session.Id && p.UserId == stranger.Id);
        Assert.NotNull(participant.DeclinedAt);
        Assert.Null(participant.JoinedAt);

        var @event = await db.Events.AsNoTracking()
            .SingleAsync(e => e.SessionId == session.Id && e.UserId == stranger.Id);
        Assert.Equal(EventKind.JoinDeclined, @event.Kind);
    }

    [Fact]
    public async Task Decline_for_ended_session_is_rejected()
    {
        var (student, session) = await SeedSessionWithStudentAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
            var tracked = await db.Sessions.SingleAsync(s => s.Id == session.Id);
            tracked.EndedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        await using var connection = BuildConnection(student.EntraOid, "Student");
        await connection.StartAsync();

        var ex = await Assert.ThrowsAsync<HubException>(() =>
            connection.InvokeAsync(
                "DeclineSession",
                new DeclineSessionRequest(session.Id, Reason: "user_cancelled")));
        Assert.Contains("already ended", ex.Message);
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
    public async Task Join_broadcasts_participant_state_changed_to_session_group()
    {
        var (student, session) = await SeedSessionWithStudentAsync();

        await using var connection = BuildConnection(student.EntraOid, "Student");
        await connection.StartAsync();
        await connection.InvokeAsync<JoinSessionResult>(
            "JoinSession", new JoinSessionRequest(session.Id, JoinCode: null));

        var broadcaster = _factory.Services.GetRequiredService<RecordingSessionBroadcaster>();
        var call = Assert.Single(broadcaster.ParticipantStateChangedCalls,
            c => c.SessionId == session.Id && c.UserId == student.Id);
        Assert.Equal(nameof(ParticipantLiveState.Joined), call.State);
        Assert.Equal("Test Student", call.DisplayName);
    }

    [Fact]
    public async Task Leave_broadcasts_left_state_to_session_group()
    {
        var (student, session) = await SeedSessionWithStudentAsync();

        await using var connection = BuildConnection(student.EntraOid, "Student");
        await connection.StartAsync();
        await connection.InvokeAsync<JoinSessionResult>(
            "JoinSession", new JoinSessionRequest(session.Id, JoinCode: null));
        await connection.InvokeAsync("LeaveSession", session.Id);

        var broadcaster = _factory.Services.GetRequiredService<RecordingSessionBroadcaster>();
        Assert.Contains(broadcaster.ParticipantStateChangedCalls,
            c => c.SessionId == session.Id && c.UserId == student.Id &&
                 c.State == nameof(ParticipantLiveState.Left));
    }

    [Fact]
    public async Task Decline_broadcasts_declined_state_to_session_group()
    {
        var (student, session) = await SeedSessionWithStudentAsync();

        await using var connection = BuildConnection(student.EntraOid, "Student");
        await connection.StartAsync();
        await connection.InvokeAsync(
            "DeclineSession", new DeclineSessionRequest(session.Id, Reason: "user_cancelled"));

        var broadcaster = _factory.Services.GetRequiredService<RecordingSessionBroadcaster>();
        Assert.Contains(broadcaster.ParticipantStateChangedCalls,
            c => c.SessionId == session.Id && c.UserId == student.Id &&
                 c.State == nameof(ParticipantLiveState.Declined));
    }

    [Fact]
    public async Task Owning_teacher_join_does_not_broadcast_participant_state()
    {
        var (_, session) = await SeedSessionWithStudentAsync();

        Guid teacherOid;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
            teacherOid = (await db.Users.AsNoTracking().SingleAsync(u => u.Id == session.TeacherId)).EntraOid;
        }

        await using var connection = BuildConnection(teacherOid, "Teacher");
        await connection.StartAsync();
        await connection.InvokeAsync<JoinSessionResult>(
            "JoinSession", new JoinSessionRequest(session.Id, JoinCode: null));

        var broadcaster = _factory.Services.GetRequiredService<RecordingSessionBroadcaster>();
        // The owning teacher is not a participant, so their own subscribe must
        // never surface on the roster.
        Assert.DoesNotContain(broadcaster.ParticipantStateChangedCalls,
            c => c.SessionId == session.Id && c.UserId == session.TeacherId);
    }

    [Fact]
    public async Task Heartbeat_from_active_participant_records_to_tracker()
    {
        var (student, session) = await SeedSessionWithStudentAsync();

        await using var connection = BuildConnection(student.EntraOid, "Student");
        await connection.StartAsync();

        await connection.InvokeAsync<JoinSessionResult>(
            "JoinSession",
            new JoinSessionRequest(session.Id, JoinCode: null));

        await connection.InvokeAsync("Heartbeat", session.Id);

        var tracker = _factory.Services.GetRequiredService<HeartbeatTracker>();
        Assert.True(tracker.TryGet(session.Id, student.Id, out _));
    }

    [Fact]
    public async Task Heartbeat_from_non_joined_user_is_rejected()
    {
        var (_, session) = await SeedSessionWithStudentAsync();
        var stranger = await SeedUserAsync(UserRole.Student, "Stranger");

        await using var connection = BuildConnection(stranger.EntraOid, "Student");
        await connection.StartAsync();

        var ex = await Assert.ThrowsAsync<HubException>(() =>
            connection.InvokeAsync("Heartbeat", session.Id));
        Assert.Contains("Not an active participant", ex.Message);

        var tracker = _factory.Services.GetRequiredService<HeartbeatTracker>();
        Assert.False(tracker.TryGet(session.Id, stranger.Id, out _));
    }

    [Fact]
    public async Task SessionEnded_broadcast_clears_heartbeat_tracker_state_for_that_session()
    {
        var (student, session) = await SeedSessionWithStudentAsync();

        await using var connection = BuildConnection(student.EntraOid, "Student");
        await connection.StartAsync();

        await connection.InvokeAsync<JoinSessionResult>(
            "JoinSession",
            new JoinSessionRequest(session.Id, JoinCode: null));
        await connection.InvokeAsync("Heartbeat", session.Id);

        var tracker = _factory.Services.GetRequiredService<HeartbeatTracker>();
        Assert.True(tracker.TryGet(session.Id, student.Id, out _));

        var broadcaster = _factory.Services.GetRequiredService<ISessionBroadcaster>();
        await broadcaster.SessionEndedAsync(session.Id);

        Assert.False(tracker.TryGet(session.Id, student.Id, out _));
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

    [Fact]
    public async Task ReportEvent_unblock_request_broadcasts_typed_payload_to_session_group()
    {
        var (student, session) = await SeedSessionWithStudentAsync();

        await using var connection = BuildConnection(student.EntraOid, "Student");
        await connection.StartAsync();
        await connection.InvokeAsync<JoinSessionResult>(
            "JoinSession", new JoinSessionRequest(session.Id, JoinCode: null));

        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            url = "https://reddit.com/r/aww",
            host = "REDDIT.com",
            reason = "research",
        });
        await connection.InvokeAsync("ReportEvent", new ReportEventRequest(
            session.Id, nameof(EventKind.UnblockRequest), payload, OccurredAt: null));

        var broadcaster = _factory.Services.GetRequiredService<RecordingSessionBroadcaster>();
        var call = Assert.Single(broadcaster.UnblockRequestedCalls,
            c => c.SessionId == session.Id && c.UserId == student.Id);
        // Host normalised to lowercase server-side so the dashboard can group
        // case-insensitive without per-row work.
        Assert.Equal("reddit.com", call.Host);
        Assert.Equal("https://reddit.com/r/aww", call.Url);
        Assert.Equal("Test Student", call.UserDisplayName);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        var @event = await db.Events.AsNoTracking()
            .SingleAsync(e => e.SessionId == session.Id && e.UserId == student.Id);
        Assert.Equal(EventKind.UnblockRequest, @event.Kind);
    }

    [Fact]
    public async Task ReportEvent_unblock_request_with_unparseable_payload_persists_event_but_skips_broadcast()
    {
        var (student, session) = await SeedSessionWithStudentAsync();

        await using var connection = BuildConnection(student.EntraOid, "Student");
        await connection.StartAsync();
        await connection.InvokeAsync<JoinSessionResult>(
            "JoinSession", new JoinSessionRequest(session.Id, JoinCode: null));

        // Payload object but without a host field — broadcast must be skipped.
        await connection.InvokeAsync("ReportEvent", new ReportEventRequest(
            session.Id, nameof(EventKind.UnblockRequest), "{\"url\":\"https://x\"}", OccurredAt: null));

        var broadcaster = _factory.Services.GetRequiredService<RecordingSessionBroadcaster>();
        Assert.DoesNotContain(broadcaster.UnblockRequestedCalls,
            c => c.SessionId == session.Id && c.UserId == student.Id);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        Assert.True(await db.Events.AnyAsync(
            e => e.SessionId == session.Id && e.UserId == student.Id && e.Kind == EventKind.UnblockRequest));
    }

    [Fact]
    public async Task ReportEvent_tamper_detected_broadcasts_kind_to_session_group()
    {
        var (student, session) = await SeedSessionWithStudentAsync();

        await using var connection = BuildConnection(student.EntraOid, "Student");
        await connection.StartAsync();
        await connection.InvokeAsync<JoinSessionResult>(
            "JoinSession", new JoinSessionRequest(session.Id, JoinCode: null));

        var payload = System.Text.Json.JsonSerializer.Serialize(new { kind = "inprivate_opened" });
        await connection.InvokeAsync("ReportEvent", new ReportEventRequest(
            session.Id, nameof(EventKind.TamperDetected), payload, OccurredAt: null));

        var broadcaster = _factory.Services.GetRequiredService<RecordingSessionBroadcaster>();
        var call = Assert.Single(broadcaster.TamperDetectedCalls,
            c => c.SessionId == session.Id && c.UserId == student.Id);
        Assert.Equal("inprivate_opened", call.Kind);
        Assert.Equal("Test Student", call.UserDisplayName);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        var @event = await db.Events.AsNoTracking()
            .SingleAsync(e => e.SessionId == session.Id && e.UserId == student.Id);
        Assert.Equal(EventKind.TamperDetected, @event.Kind);
    }

    [Fact]
    public async Task ReportEvent_tamper_detected_with_unparseable_payload_still_broadcasts_unknown()
    {
        var (student, session) = await SeedSessionWithStudentAsync();

        await using var connection = BuildConnection(student.EntraOid, "Student");
        await connection.StartAsync();
        await connection.InvokeAsync<JoinSessionResult>(
            "JoinSession", new JoinSessionRequest(session.Id, JoinCode: null));

        // No "kind" field — unlike UnblockRequest, the broadcast must NOT be
        // skipped: a tamper happened regardless, so the teacher is still flagged
        // with kind "unknown" (#105, design §5.4).
        await connection.InvokeAsync("ReportEvent", new ReportEventRequest(
            session.Id, nameof(EventKind.TamperDetected), "{\"oops\":true}", OccurredAt: null));

        var broadcaster = _factory.Services.GetRequiredService<RecordingSessionBroadcaster>();
        var call = Assert.Single(broadcaster.TamperDetectedCalls,
            c => c.SessionId == session.Id && c.UserId == student.Id);
        Assert.Equal("unknown", call.Kind);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        Assert.True(await db.Events.AnyAsync(
            e => e.SessionId == session.Id && e.UserId == student.Id && e.Kind == EventKind.TamperDetected));
    }

    [Fact]
    public async Task SessionStarted_REST_call_reaches_roster_members_only()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory, studentCount: 2);
        var outsider = await TestSeed.AddUserAsync(_factory, UserRole.Student, "Outsider");

        await using var studentA = BuildConnection(scenario.Students[0].EntraOid, "Student");
        await using var studentB = BuildConnection(scenario.Students[1].EntraOid, "Student");
        await using var outsideConn = BuildConnection(outsider.EntraOid, "Student");

        var signalA = new TaskCompletionSource<SessionStartedPayload>();
        var signalB = new TaskCompletionSource<SessionStartedPayload>();
        var signalOutsider = new TaskCompletionSource<SessionStartedPayload>();
        studentA.On<SessionStartedPayload>(nameof(ISessionHubClient.SessionStarted), p => signalA.TrySetResult(p));
        studentB.On<SessionStartedPayload>(nameof(ISessionHubClient.SessionStarted), p => signalB.TrySetResult(p));
        outsideConn.On<SessionStartedPayload>(nameof(ISessionHubClient.SessionStarted), p => signalOutsider.TrySetResult(p));

        await studentA.StartAsync();
        await studentB.StartAsync();
        await outsideConn.StartAsync();

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);
        var response = await client.PostAsJsonAsync(
            "/sessions",
            new StartSessionRequest(scenario.Class.Id, null));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<StartSessionResponse>();
        Assert.NotNull(body);

        var receivedA = await signalA.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var receivedB = await signalB.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(body!.Id, receivedA.SessionId);
        Assert.Equal(body.Id, receivedB.SessionId);

        var outsiderGotIt = await Task.WhenAny(signalOutsider.Task, Task.Delay(500)) == signalOutsider.Task;
        Assert.False(outsiderGotIt, "User outside the class roster should not receive SessionStarted.");
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
