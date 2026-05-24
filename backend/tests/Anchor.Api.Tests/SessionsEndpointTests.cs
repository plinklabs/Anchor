using System.Net;
using System.Net.Http.Json;
using Anchor.Api.Controllers;
using Anchor.Api.Realtime;
using Anchor.Api.Tests.FakeAuth;
using Anchor.Domain.Bundles;
using Anchor.Domain.Sessions;
using Anchor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Anchor.Api.Tests;

public sealed class SessionsEndpointTests : IClassFixture<AnchorApiFactory>
{
    private readonly AnchorApiFactory _factory;

    public SessionsEndpointTests(AnchorApiFactory factory)
    {
        _factory = factory;
    }

    // ------- POST /sessions -------

    [Fact]
    public async Task POST_sessions_unauthenticated_returns_401()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);

        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/sessions", new StartSessionRequest(scenario.Class.Id, "Strict", null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task POST_sessions_as_student_returns_403()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);

        using var client = _factory.CreateClient();
        TestAuth.SetStudent(client, scenario.Students[0]);

        var response = await client.PostAsJsonAsync("/sessions", new StartSessionRequest(scenario.Class.Id, "Strict", null));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task POST_sessions_for_class_caller_does_not_teach_returns_403()
    {
        var owned = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var other = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, owned.Teacher);

        var response = await client.PostAsJsonAsync("/sessions", new StartSessionRequest(other.Class.Id, "Strict", null));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task POST_sessions_for_missing_class_returns_404()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var response = await client.PostAsJsonAsync("/sessions", new StartSessionRequest(Guid.NewGuid(), "Strict", null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task POST_sessions_with_unknown_mode_returns_400()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var response = await client.PostAsJsonAsync("/sessions", new StartSessionRequest(scenario.Class.Id, "Bogus", null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task POST_sessions_with_unknown_bundle_returns_400()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var response = await client.PostAsJsonAsync(
            "/sessions",
            new StartSessionRequest(scenario.Class.Id, "Strict", new[] { Guid.NewGuid() }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task POST_sessions_happy_path_creates_session_and_broadcasts()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory, studentCount: 2);
        var bundle = await TestSeed.AddBundleAsync(_factory, "Microsoft 365");
        await TestSeed.AddBundleEntryAsync(_factory, bundle.Id, BundleEntryKind.Domain, "*.smartschool.be", BundleEntryMatchType.Wildcard);

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var response = await client.PostAsJsonAsync(
            "/sessions",
            new StartSessionRequest(scenario.Class.Id, "Strict", new[] { bundle.Id }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<StartSessionResponse>();
        Assert.NotNull(body);
        Assert.Equal(scenario.Class.Id, body!.ClassId);
        Assert.Equal(SessionMode.Strict, body.Mode);
        Assert.Equal(6, body.JoinCode.Length);
        Assert.True(body.JoinCode.All(char.IsDigit), $"JoinCode '{body.JoinCode}' should be all digits.");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();

        var session = await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == body.Id);
        Assert.Equal(scenario.Teacher.Id, session.TeacherId);

        var participants = await db.SessionParticipants.AsNoTracking()
            .Where(p => p.SessionId == body.Id)
            .ToListAsync();
        Assert.Equal(scenario.Students.Count, participants.Count);
        foreach (var student in scenario.Students)
            Assert.Contains(participants, p => p.UserId == student.Id);

        var sessionBundles = await db.SessionBundles.AsNoTracking()
            .Where(sb => sb.SessionId == body.Id)
            .ToListAsync();
        Assert.Single(sessionBundles);
        Assert.Equal(bundle.Id, sessionBundles[0].BundleId);

        var broadcaster = _factory.Services.GetRequiredService<RecordingSessionBroadcaster>();
        var broadcast = Assert.Single(broadcaster.SessionStartedCalls, p => p.SessionId == body.Id);
        Assert.Equal(body.JoinCode, broadcast.JoinCode);
        // Bundle entry made it onto the wire alongside the baseline (#70).
        Assert.Contains(broadcast.Payload.Domains, d => d.Value == "*.smartschool.be" && d.MatchType == "Wildcard");
        Assert.Contains(broadcast.Payload.Apps, a => a.Value == "msedge" && a.MatchKind == "ProcessName");
    }

    [Fact]
    public async Task POST_sessions_with_no_bundles_still_broadcasts_baseline_allowlist()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory, studentCount: 1);

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var response = await client.PostAsJsonAsync(
            "/sessions",
            new StartSessionRequest(scenario.Class.Id, "Strict", null));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<StartSessionResponse>();

        var broadcaster = _factory.Services.GetRequiredService<RecordingSessionBroadcaster>();
        var broadcast = Assert.Single(broadcaster.SessionStartedCalls, p => p.SessionId == body!.Id);
        Assert.NotEmpty(broadcast.Payload.Apps);
        Assert.NotEmpty(broadcast.Payload.Domains);
    }

    // ------- POST /sessions/{id}/end -------

    [Fact]
    public async Task POST_session_end_unauthenticated_returns_401()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var session = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList());

        using var client = _factory.CreateClient();
        var response = await client.PostAsync($"/sessions/{session.Id}/end", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task POST_session_end_as_student_returns_403()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var session = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList());

        using var client = _factory.CreateClient();
        TestAuth.SetStudent(client, scenario.Students[0]);

        var response = await client.PostAsync($"/sessions/{session.Id}/end", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task POST_session_end_by_non_owning_teacher_returns_403()
    {
        var owned = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var other = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var session = await TestSeed.AddSessionAsync(
            _factory, owned.Teacher.Id, owned.Class.Id, owned.Students.Select(s => s.Id).ToList());

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, other.Teacher);

        var response = await client.PostAsync($"/sessions/{session.Id}/end", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task POST_session_end_for_missing_session_returns_404()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var response = await client.PostAsync($"/sessions/{Guid.NewGuid()}/end", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task POST_session_end_happy_path_sets_ended_at_and_broadcasts()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var session = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList());

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var response = await client.PostAsync($"/sessions/{session.Id}/end", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<EndSessionResponse>();
        Assert.NotNull(body);
        Assert.Equal(session.Id, body!.Id);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        var persisted = await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == session.Id);
        Assert.NotNull(persisted.EndedAt);

        var broadcaster = _factory.Services.GetRequiredService<RecordingSessionBroadcaster>();
        Assert.Contains(session.Id, broadcaster.SessionEndedCalls);
    }

    [Fact]
    public async Task POST_session_end_is_idempotent_when_already_ended()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var session = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList(), ended: true);

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var response = await client.PostAsync($"/sessions/{session.Id}/end", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ------- GET /sessions/{id} -------

    [Fact]
    public async Task GET_session_unauthenticated_returns_401()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var session = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList());

        using var client = _factory.CreateClient();
        var response = await client.GetAsync($"/sessions/{session.Id}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GET_session_returns_404_for_missing_session()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var response = await client.GetAsync($"/sessions/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GET_session_returns_403_when_caller_is_neither_owner_nor_participant()
    {
        var owned = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var session = await TestSeed.AddSessionAsync(
            _factory, owned.Teacher.Id, owned.Class.Id, owned.Students.Select(s => s.Id).ToList());

        var outsider = await TestSeed.AddUserAsync(_factory, Anchor.Domain.Users.UserRole.Student, "Outsider");

        using var client = _factory.CreateClient();
        TestAuth.SetStudent(client, outsider);

        var response = await client.GetAsync($"/sessions/{session.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GET_session_returns_detail_for_owning_teacher()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var session = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList());

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var body = await client.GetFromJsonAsync<SessionDetailResponse>($"/sessions/{session.Id}");

        Assert.NotNull(body);
        Assert.Equal(session.Id, body!.Id);
        Assert.Equal(scenario.Students.Count, body.Participants.Count);
        Assert.Empty(body.RecentEvents);
    }

    [Fact]
    public async Task GET_session_returns_detail_for_participating_student()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var session = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList());

        using var client = _factory.CreateClient();
        TestAuth.SetStudent(client, scenario.Students[0]);

        var response = await client.GetAsync($"/sessions/{session.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ------- GET /sessions/active -------

    [Fact]
    public async Task GET_sessions_active_unauthenticated_returns_401()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/sessions/active");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GET_sessions_active_for_teacher_returns_own_running_sessions()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var live = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList());
        // Ended session — should not appear.
        await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList(), ended: true);
        // Session owned by a different teacher — should not appear.
        var other = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        await TestSeed.AddSessionAsync(
            _factory, other.Teacher.Id, other.Class.Id, other.Students.Select(s => s.Id).ToList());

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var body = await client.GetFromJsonAsync<List<SessionSummary>>("/sessions/active");

        Assert.NotNull(body);
        Assert.Single(body!);
        Assert.Equal(live.Id, body![0].Id);
    }

    [Fact]
    public async Task GET_sessions_active_for_student_returns_sessions_they_participate_in()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var live = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList());
        // Another class the student isn't part of.
        var other = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        await TestSeed.AddSessionAsync(
            _factory, other.Teacher.Id, other.Class.Id, other.Students.Select(s => s.Id).ToList());

        using var client = _factory.CreateClient();
        TestAuth.SetStudent(client, scenario.Students[0]);

        var body = await client.GetFromJsonAsync<List<SessionSummary>>("/sessions/active");

        Assert.NotNull(body);
        Assert.Single(body!);
        Assert.Equal(live.Id, body![0].Id);
    }

    // ------- GET /sessions/rejoinable -------

    [Fact]
    public async Task GET_sessions_rejoinable_unauthenticated_returns_401()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/sessions/rejoinable");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GET_sessions_rejoinable_returns_only_actively_joined_non_declined_non_left_non_ended_sessions()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory, studentCount: 1);
        var student = scenario.Students[0];

        // Session A: actively joined (JoinedAt set, LeftAt null, DeclinedAt null) — should appear.
        var joined = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, new[] { student.Id });
        await SetParticipantStateAsync(joined.Id, student.Id, joinedAt: DateTimeOffset.UtcNow.AddMinutes(-2));

        // Session B: declined — should NOT appear.
        var declined = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, new[] { student.Id });
        await SetParticipantStateAsync(declined.Id, student.Id, declinedAt: DateTimeOffset.UtcNow.AddMinutes(-2));

        // Session C: left after joining — should NOT appear.
        var left = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, new[] { student.Id });
        await SetParticipantStateAsync(left.Id, student.Id,
            joinedAt: DateTimeOffset.UtcNow.AddMinutes(-3),
            leftAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        // Session D: never joined (no JoinedAt) — should NOT appear (agent restart shouldn't rejoin
        // a session the student never confirmed in the first place).
        await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, new[] { student.Id });

        // Session E: ended — should NOT appear even though student was actively joined.
        var ended = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, new[] { student.Id }, ended: true);
        await SetParticipantStateAsync(ended.Id, student.Id, joinedAt: DateTimeOffset.UtcNow.AddMinutes(-10));

        // Session F: a session the student isn't a participant of at all — should NOT appear.
        var foreign = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        await TestSeed.AddSessionAsync(
            _factory, foreign.Teacher.Id, foreign.Class.Id, foreign.Students.Select(s => s.Id).ToList());

        using var client = _factory.CreateClient();
        TestAuth.SetStudent(client, student);

        var body = await client.GetFromJsonAsync<List<SessionStartedPayload>>("/sessions/rejoinable");

        Assert.NotNull(body);
        Assert.Single(body!);
        Assert.Equal(joined.Id, body![0].SessionId);
        // Carries the expanded allowlist so the agent can rejoin with the
        // same rules a fresh broadcast would have carried (#70). With no
        // bundles attached, only the baseline shows up.
        Assert.NotEmpty(body[0].Apps);
        Assert.NotEmpty(body[0].Domains);
    }

    [Fact]
    public async Task GET_sessions_rejoinable_includes_bundle_expansion()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory, studentCount: 1);
        var student = scenario.Students[0];
        var bundle = await TestSeed.AddBundleAsync(_factory, "Smartschool-rejoin");
        await TestSeed.AddBundleEntryAsync(_factory, bundle.Id, BundleEntryKind.Domain, "*.rejoin.example", BundleEntryMatchType.Wildcard);

        var session = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, new[] { student.Id });
        await AttachBundleAsync(session.Id, bundle.Id);
        await SetParticipantStateAsync(session.Id, student.Id, joinedAt: DateTimeOffset.UtcNow.AddMinutes(-2));

        using var client = _factory.CreateClient();
        TestAuth.SetStudent(client, student);

        var body = await client.GetFromJsonAsync<List<SessionStartedPayload>>("/sessions/rejoinable");

        Assert.NotNull(body);
        var entry = Assert.Single(body!);
        Assert.Contains(entry.Domains, d => d.Value == "*.rejoin.example");
    }

    [Fact]
    public async Task GET_sessions_rejoinable_for_teacher_returns_empty()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        // Teacher has a running session but isn't a participant row — rejoin
        // is a student-side concept.
        var session = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList());
        await SetParticipantStateAsync(session.Id, scenario.Students[0].Id, joinedAt: DateTimeOffset.UtcNow);

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var body = await client.GetFromJsonAsync<List<SessionStartedPayload>>("/sessions/rejoinable");

        Assert.NotNull(body);
        Assert.Empty(body!);
    }

    private async Task AttachBundleAsync(Guid sessionId, Guid bundleId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        db.SessionBundles.Add(new SessionBundle { SessionId = sessionId, BundleId = bundleId });
        await db.SaveChangesAsync();
    }

    private async Task SetParticipantStateAsync(
        Guid sessionId,
        Guid userId,
        DateTimeOffset? joinedAt = null,
        DateTimeOffset? declinedAt = null,
        DateTimeOffset? leftAt = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        var participant = await db.SessionParticipants
            .SingleAsync(p => p.SessionId == sessionId && p.UserId == userId);
        if (joinedAt.HasValue) participant.JoinedAt = joinedAt;
        if (declinedAt.HasValue) participant.DeclinedAt = declinedAt;
        if (leftAt.HasValue) participant.LeftAt = leftAt;
        await db.SaveChangesAsync();
    }
}
