using System.Net;
using System.Net.Http.Json;
using Anchor.Api.Controllers;
using Anchor.Api.Realtime;
using Anchor.Api.Tests.FakeAuth;
using Anchor.Domain.Bundles;
using Anchor.Domain.Events;
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
        var response = await client.PostAsJsonAsync("/sessions", new StartSessionRequest(scenario.Class.Id, null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task POST_sessions_as_student_returns_403()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);

        using var client = _factory.CreateClient();
        TestAuth.SetStudent(client, scenario.Students[0]);

        var response = await client.PostAsJsonAsync("/sessions", new StartSessionRequest(scenario.Class.Id, null));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task POST_sessions_for_class_caller_does_not_teach_returns_403()
    {
        var owned = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var other = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, owned.Teacher);

        var response = await client.PostAsJsonAsync("/sessions", new StartSessionRequest(other.Class.Id, null));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task POST_sessions_for_missing_class_returns_404()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var response = await client.PostAsJsonAsync("/sessions", new StartSessionRequest(Guid.NewGuid(), null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task POST_sessions_with_unknown_bundle_returns_400()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var response = await client.PostAsJsonAsync(
            "/sessions",
            new StartSessionRequest(scenario.Class.Id, new[] { Guid.NewGuid() }));

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
            new StartSessionRequest(scenario.Class.Id, new[] { bundle.Id }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<StartSessionResponse>();
        Assert.NotNull(body);
        Assert.Equal(scenario.Class.Id, body!.ClassId);
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
            new StartSessionRequest(scenario.Class.Id, null));

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
    public async Task POST_session_end_writes_summary_rows_matching_group_by_on_raw_events()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory, studentCount: 2);
        var session = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList());

        // Seed a known mix of events: 3 ForegroundChange + 2 BlockedUrl for s0,
        // 1 ForegroundChange for s1. Expected summary rows: 3 total
        // ((s0, FG, 3), (s0, BU, 2), (s1, FG, 1)).
        var s0 = scenario.Students[0].Id;
        var s1 = scenario.Students[1].Id;
        await SeedEventsAsync(session.Id, new[]
        {
            (s0, EventKind.ForegroundChange, new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero)),
            (s0, EventKind.ForegroundChange, new DateTimeOffset(2026, 1, 1, 9, 5, 0, TimeSpan.Zero)),
            (s0, EventKind.ForegroundChange, new DateTimeOffset(2026, 1, 1, 9, 10, 0, TimeSpan.Zero)),
            (s0, EventKind.BlockedUrl,       new DateTimeOffset(2026, 1, 1, 9, 1, 0, TimeSpan.Zero)),
            (s0, EventKind.BlockedUrl,       new DateTimeOffset(2026, 1, 1, 9, 6, 0, TimeSpan.Zero)),
            (s1, EventKind.ForegroundChange, new DateTimeOffset(2026, 1, 1, 9, 7, 0, TimeSpan.Zero)),
        });

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);
        var response = await client.PostAsync($"/sessions/{session.Id}/end", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        var summaries = await db.SessionEventSummaries.AsNoTracking()
            .Where(s => s.SessionId == session.Id)
            .ToListAsync();
        Assert.Equal(3, summaries.Count);

        var s0Fg = summaries.Single(s => s.UserId == s0 && s.Kind == EventKind.ForegroundChange);
        Assert.Equal(3, s0Fg.Count);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero), s0Fg.FirstAt);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 9, 10, 0, TimeSpan.Zero), s0Fg.LastAt);

        var s0Bu = summaries.Single(s => s.UserId == s0 && s.Kind == EventKind.BlockedUrl);
        Assert.Equal(2, s0Bu.Count);

        var s1Fg = summaries.Single(s => s.UserId == s1 && s.Kind == EventKind.ForegroundChange);
        Assert.Equal(1, s1Fg.Count);
    }

    [Fact]
    public async Task POST_session_end_with_no_events_writes_no_summary_rows()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var session = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList());

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);
        var response = await client.PostAsync($"/sessions/{session.Id}/end", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        Assert.False(await db.SessionEventSummaries.AnyAsync(s => s.SessionId == session.Id));
    }

    [Fact]
    public async Task POST_session_end_does_not_double_write_summaries_when_called_twice()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var session = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList());
        var s0 = scenario.Students[0].Id;
        await SeedEventsAsync(session.Id, new[]
        {
            (s0, EventKind.ForegroundChange, new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero)),
            (s0, EventKind.ForegroundChange, new DateTimeOffset(2026, 1, 1, 9, 1, 0, TimeSpan.Zero)),
        });

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);
        await client.PostAsync($"/sessions/{session.Id}/end", null);
        // Second call must short-circuit on session.EndedAt != null and
        // produce no extra summary rows (PK collision would also surface here).
        var second = await client.PostAsync($"/sessions/{session.Id}/end", null);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        var row = await db.SessionEventSummaries.AsNoTracking()
            .SingleAsync(s => s.SessionId == session.Id);
        Assert.Equal(2, row.Count);
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

    // ------- PUT /sessions/{id}/bundles -------

    [Fact]
    public async Task PUT_session_bundles_unauthenticated_returns_401()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var session = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList());

        using var client = _factory.CreateClient();
        var response = await client.PutAsJsonAsync(
            $"/sessions/{session.Id}/bundles", new UpdateSessionBundlesRequest(Array.Empty<Guid>()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PUT_session_bundles_as_student_returns_403()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var session = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList());

        using var client = _factory.CreateClient();
        TestAuth.SetStudent(client, scenario.Students[0]);

        var response = await client.PutAsJsonAsync(
            $"/sessions/{session.Id}/bundles", new UpdateSessionBundlesRequest(Array.Empty<Guid>()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PUT_session_bundles_by_non_owning_teacher_returns_403()
    {
        var owned = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var other = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var session = await TestSeed.AddSessionAsync(
            _factory, owned.Teacher.Id, owned.Class.Id, owned.Students.Select(s => s.Id).ToList());

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, other.Teacher);

        var response = await client.PutAsJsonAsync(
            $"/sessions/{session.Id}/bundles", new UpdateSessionBundlesRequest(Array.Empty<Guid>()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PUT_session_bundles_for_missing_session_returns_404()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var response = await client.PutAsJsonAsync(
            $"/sessions/{Guid.NewGuid()}/bundles", new UpdateSessionBundlesRequest(Array.Empty<Guid>()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PUT_session_bundles_for_ended_session_returns_400()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var session = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList(), ended: true);

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var response = await client.PutAsJsonAsync(
            $"/sessions/{session.Id}/bundles", new UpdateSessionBundlesRequest(Array.Empty<Guid>()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PUT_session_bundles_with_unknown_bundle_returns_400()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var session = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList());

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var response = await client.PutAsJsonAsync(
            $"/sessions/{session.Id}/bundles", new UpdateSessionBundlesRequest(new[] { Guid.NewGuid() }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PUT_session_bundles_replaces_set_and_broadcasts_to_active_participants()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory, studentCount: 2);
        var oldBundle = await TestSeed.AddBundleAsync(_factory, "PutBundles-Old");
        var newBundle = await TestSeed.AddBundleAsync(_factory, "PutBundles-New");
        await TestSeed.AddBundleEntryAsync(_factory, newBundle.Id, BundleEntryKind.Domain, "*.smartschool.be", BundleEntryMatchType.Wildcard);

        var session = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList());
        await AttachBundleAsync(session.Id, oldBundle.Id);
        // Only student 0 is actively joined; student 1 hasn't confirmed yet.
        await SetParticipantStateAsync(session.Id, scenario.Students[0].Id, joinedAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        var broadcaster = _factory.Services.GetRequiredService<RecordingSessionBroadcaster>();
        broadcaster.SessionBundlesUpdatedCalls.Clear();

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var response = await client.PutAsJsonAsync(
            $"/sessions/{session.Id}/bundles", new UpdateSessionBundlesRequest(new[] { newBundle.Id }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<UpdateSessionBundlesResponse>();
        Assert.NotNull(body);
        var returned = Assert.Single(body!.Bundles);
        Assert.Equal(newBundle.Id, returned.Id);

        // Old bundle is gone, new bundle persisted (replace, not append).
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        var persisted = await db.SessionBundles.AsNoTracking()
            .Where(sb => sb.SessionId == session.Id)
            .Select(sb => sb.BundleId)
            .ToListAsync();
        Assert.Equal(new[] { newBundle.Id }, persisted);

        // Only the actively-joined student gets the push.
        var call = Assert.Single(broadcaster.SessionBundlesUpdatedCalls, c => c.SessionId == session.Id);
        Assert.Equal(scenario.Students[0].Id, call.UserId);
        Assert.Contains(call.Payload.Domains, d => d.Value == "*.smartschool.be" && d.MatchType == "Wildcard");
        Assert.Contains(call.Payload.Apps, a => a.Value == "msedge" && a.MatchKind == "ProcessName");
    }

    [Fact]
    public async Task PUT_session_bundles_folds_each_students_grants_into_their_payload()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory, studentCount: 2);
        var session = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList());
        await SetParticipantStateAsync(session.Id, scenario.Students[0].Id, joinedAt: DateTimeOffset.UtcNow.AddMinutes(-2));
        await SetParticipantStateAsync(session.Id, scenario.Students[1].Id, joinedAt: DateTimeOffset.UtcNow.AddMinutes(-2));
        // Student 0 has an approved unblock grant; student 1 does not.
        await AddUnblockGrantAsync(session.Id, scenario.Students[0].Id, "reddit.com",
            new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero));

        var broadcaster = _factory.Services.GetRequiredService<RecordingSessionBroadcaster>();
        broadcaster.SessionBundlesUpdatedCalls.Clear();

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var response = await client.PutAsJsonAsync(
            $"/sessions/{session.Id}/bundles", new UpdateSessionBundlesRequest(Array.Empty<Guid>()));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var calls = broadcaster.SessionBundlesUpdatedCalls.Where(c => c.SessionId == session.Id).ToList();
        Assert.Equal(2, calls.Count);

        var withGrant = Assert.Single(calls, c => c.UserId == scenario.Students[0].Id);
        Assert.Contains(withGrant.Payload.Domains, d => d.Value == "reddit.com" && d.MatchType == "Suffix");

        var withoutGrant = Assert.Single(calls, c => c.UserId == scenario.Students[1].Id);
        Assert.DoesNotContain(withoutGrant.Payload.Domains, d => d.Value == "reddit.com");
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
    public async Task GET_session_resolves_per_participant_live_state_including_heartbeat_stale()
    {
        // One student per state (#100): never-joined (untouched), joined+fresh,
        // joined+stale, declined, left.
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory, studentCount: 5);
        var session = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList());

        var never = scenario.Students[0];
        var joinedFresh = scenario.Students[1];
        var joinedStale = scenario.Students[2];
        var declined = scenario.Students[3];
        var left = scenario.Students[4];

        await SetParticipantStateAsync(session.Id, joinedFresh.Id, joinedAt: DateTimeOffset.UtcNow.AddMinutes(-2));
        await SetParticipantStateAsync(session.Id, joinedStale.Id, joinedAt: DateTimeOffset.UtcNow.AddMinutes(-2));
        await SetParticipantStateAsync(session.Id, declined.Id, declinedAt: DateTimeOffset.UtcNow.AddMinutes(-2));
        await SetParticipantStateAsync(session.Id, left.Id,
            joinedAt: DateTimeOffset.UtcNow.AddMinutes(-3), leftAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        // Heartbeat liveness lives in the singleton tracker, not the DB. A fresh
        // ping reads Joined; a ping older than the 20s default timeout reads stale.
        var tracker = _factory.Services.GetRequiredService<HeartbeatTracker>();
        tracker.Record(session.Id, joinedFresh.Id, DateTimeOffset.UtcNow);
        tracker.Record(session.Id, joinedStale.Id, DateTimeOffset.UtcNow.AddMinutes(-1));

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var body = await client.GetFromJsonAsync<SessionDetailResponse>($"/sessions/{session.Id}");

        Assert.NotNull(body);
        string StateOf(Guid userId) => body!.Participants.Single(p => p.UserId == userId).State;
        Assert.Equal(nameof(ParticipantLiveState.NeverJoined), StateOf(never.Id));
        Assert.Equal(nameof(ParticipantLiveState.Joined), StateOf(joinedFresh.Id));
        Assert.Equal(nameof(ParticipantLiveState.HeartbeatStale), StateOf(joinedStale.Id));
        Assert.Equal(nameof(ParticipantLiveState.Declined), StateOf(declined.Id));
        Assert.Equal(nameof(ParticipantLiveState.Left), StateOf(left.Id));
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

    [Fact]
    public async Task GET_session_detail_includes_attached_bundles_with_names()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var bundleA = await TestSeed.AddBundleAsync(_factory, "Smartschool");
        var bundleB = await TestSeed.AddBundleAsync(_factory, "Wiskunde");
        var session = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList());
        await AttachBundleAsync(session.Id, bundleA.Id);
        await AttachBundleAsync(session.Id, bundleB.Id);

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var body = await client.GetFromJsonAsync<SessionDetailResponse>($"/sessions/{session.Id}");

        Assert.NotNull(body);
        Assert.Equal(2, body!.Bundles.Count);
        Assert.Contains(body.Bundles, b => b.Id == bundleA.Id && b.Name == "Smartschool");
        Assert.Contains(body.Bundles, b => b.Id == bundleB.Id && b.Name == "Wiskunde");
    }

    [Fact]
    public async Task GET_session_detail_includes_unblock_grants_with_display_names()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory, studentCount: 2);
        var session = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList());
        await AddUnblockGrantAsync(session.Id, scenario.Students[0].Id, "reddit.com",
            new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero));
        await AddUnblockGrantAsync(session.Id, scenario.Students[1].Id, "youtube.com",
            new DateTimeOffset(2026, 1, 1, 9, 5, 0, TimeSpan.Zero));

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var body = await client.GetFromJsonAsync<SessionDetailResponse>($"/sessions/{session.Id}");

        Assert.NotNull(body);
        Assert.Equal(2, body!.Grants.Count);
        // GrantedAt ordering: earliest first.
        Assert.Equal("reddit.com", body.Grants[0].Host);
        Assert.Equal(scenario.Students[0].DisplayName, body.Grants[0].DisplayName);
        Assert.Equal("youtube.com", body.Grants[1].Host);
        Assert.Equal(scenario.Students[1].DisplayName, body.Grants[1].DisplayName);
    }

    private async Task AddUnblockGrantAsync(Guid sessionId, Guid userId, string host, DateTimeOffset grantedAt)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        db.SessionUnblockGrants.Add(new Anchor.Domain.Sessions.SessionUnblockGrant
        {
            SessionId = sessionId,
            UserId = userId,
            Host = host,
            GrantedAt = grantedAt,
        });
        await db.SaveChangesAsync();
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

    // ------- GET /sessions/history -------

    [Fact]
    public async Task GET_sessions_history_unauthenticated_returns_401()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/sessions/history");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GET_sessions_history_as_student_returns_403()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);

        using var client = _factory.CreateClient();
        TestAuth.SetStudent(client, scenario.Students[0]);

        var response = await client.GetAsync("/sessions/history");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GET_sessions_history_returns_only_callers_ended_sessions_ordered_by_ended_at_desc()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        // Three ended sessions for the calling teacher, distinct EndedAt to make
        // ordering observable. AddSessionAsync's default EndedAt only differs by
        // wall-clock drift, so pin the values explicitly.
        var older = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList(), ended: true);
        var middle = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList(), ended: true);
        var newest = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList(), ended: true);
        await SetSessionEndedAtAsync(older.Id, new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero));
        await SetSessionEndedAtAsync(middle.Id, new DateTimeOffset(2026, 1, 2, 9, 0, 0, TimeSpan.Zero));
        await SetSessionEndedAtAsync(newest.Id, new DateTimeOffset(2026, 1, 3, 9, 0, 0, TimeSpan.Zero));

        // Live (not-ended) session for same teacher — must not appear.
        await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList());

        // Another teacher's ended session — must not appear.
        var other = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        await TestSeed.AddSessionAsync(
            _factory, other.Teacher.Id, other.Class.Id, other.Students.Select(s => s.Id).ToList(), ended: true);

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var body = await client.GetFromJsonAsync<List<SessionHistoryEntry>>("/sessions/history");

        Assert.NotNull(body);
        Assert.Equal(3, body!.Count);
        Assert.Equal(newest.Id, body[0].Id);
        Assert.Equal(middle.Id, body[1].Id);
        Assert.Equal(older.Id, body[2].Id);
        // Class name comes from a join, not the live class lookup the dashboard
        // already has — exposing it here saves the page an extra round trip.
        Assert.Equal(scenario.Class.Name, body[0].ClassName);
    }

    [Fact]
    public async Task GET_sessions_history_paginates_with_limit_and_offset()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var sessions = new List<Session>();
        for (var i = 0; i < 5; i++)
        {
            var s = await TestSeed.AddSessionAsync(
                _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(x => x.Id).ToList(), ended: true);
            await SetSessionEndedAtAsync(s.Id, new DateTimeOffset(2026, 1, i + 1, 9, 0, 0, TimeSpan.Zero));
            sessions.Add(s);
        }

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var firstPage = await client.GetFromJsonAsync<List<SessionHistoryEntry>>("/sessions/history?limit=2");
        Assert.Equal(2, firstPage!.Count);
        Assert.Equal(sessions[4].Id, firstPage[0].Id);
        Assert.Equal(sessions[3].Id, firstPage[1].Id);

        var secondPage = await client.GetFromJsonAsync<List<SessionHistoryEntry>>("/sessions/history?limit=2&offset=2");
        Assert.Equal(2, secondPage!.Count);
        Assert.Equal(sessions[2].Id, secondPage[0].Id);
        Assert.Equal(sessions[1].Id, secondPage[1].Id);

        var thirdPage = await client.GetFromJsonAsync<List<SessionHistoryEntry>>("/sessions/history?limit=2&offset=4");
        Assert.Single(thirdPage!);
        Assert.Equal(sessions[0].Id, thirdPage![0].Id);
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=201")]
    [InlineData("offset=-1")]
    public async Task GET_sessions_history_rejects_invalid_pagination(string query)
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var response = await client.GetAsync($"/sessions/history?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GET_sessions_history_empty_when_caller_has_no_ended_sessions()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList());

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var body = await client.GetFromJsonAsync<List<SessionHistoryEntry>>("/sessions/history");

        Assert.NotNull(body);
        Assert.Empty(body!);
    }

    private async Task SetSessionEndedAtAsync(Guid sessionId, DateTimeOffset endedAt)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        var session = await db.Sessions.SingleAsync(s => s.Id == sessionId);
        session.EndedAt = endedAt;
        await db.SaveChangesAsync();
    }

    private async Task AttachBundleAsync(Guid sessionId, Guid bundleId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        db.SessionBundles.Add(new SessionBundle { SessionId = sessionId, BundleId = bundleId });
        await db.SaveChangesAsync();
    }

    private async Task SeedEventsAsync(
        Guid sessionId,
        IEnumerable<(Guid UserId, EventKind Kind, DateTimeOffset OccurredAt)> events)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        foreach (var (userId, kind, occurredAt) in events)
        {
            db.Events.Add(new Event
            {
                SessionId = sessionId,
                UserId = userId,
                Kind = kind,
                PayloadJson = "{}",
                OccurredAt = occurredAt,
            });
        }
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
