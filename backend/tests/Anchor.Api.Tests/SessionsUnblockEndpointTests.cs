using System.Net;
using System.Net.Http.Json;
using Anchor.Api.Controllers;
using Anchor.Api.Realtime;
using Anchor.Api.Sessions;
using Anchor.Api.Tests.FakeAuth;
using Anchor.Domain.Events;
using Anchor.Domain.Sessions;
using Anchor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Anchor.Api.Tests;

public sealed class SessionsUnblockEndpointTests : IClassFixture<AnchorApiFactory>
{
    private readonly AnchorApiFactory _factory;

    public SessionsUnblockEndpointTests(AnchorApiFactory factory)
    {
        _factory = factory;
    }

    // ------- POST /sessions/{id}/unblock -------

    [Fact]
    public async Task POST_unblock_unauthenticated_returns_401()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var session = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList());

        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            $"/sessions/{session.Id}/unblock",
            new UnblockGrantRequest(scenario.Students[0].Id, "reddit.com"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task POST_unblock_as_student_returns_403()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var session = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList());

        using var client = _factory.CreateClient();
        TestAuth.SetStudent(client, scenario.Students[0]);

        var response = await client.PostAsJsonAsync(
            $"/sessions/{session.Id}/unblock",
            new UnblockGrantRequest(scenario.Students[0].Id, "reddit.com"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task POST_unblock_by_non_owning_teacher_returns_403()
    {
        var owned = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var other = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var session = await TestSeed.AddSessionAsync(
            _factory, owned.Teacher.Id, owned.Class.Id, owned.Students.Select(s => s.Id).ToList());

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, other.Teacher);

        var response = await client.PostAsJsonAsync(
            $"/sessions/{session.Id}/unblock",
            new UnblockGrantRequest(owned.Students[0].Id, "reddit.com"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task POST_unblock_for_missing_session_returns_404()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var response = await client.PostAsJsonAsync(
            $"/sessions/{Guid.NewGuid()}/unblock",
            new UnblockGrantRequest(scenario.Students[0].Id, "reddit.com"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task POST_unblock_for_ended_session_returns_400()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var session = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList(), ended: true);

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var response = await client.PostAsJsonAsync(
            $"/sessions/{session.Id}/unblock",
            new UnblockGrantRequest(scenario.Students[0].Id, "reddit.com"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task POST_unblock_for_non_participant_returns_400()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var outsider = await TestSeed.AddUserAsync(_factory, Anchor.Domain.Users.UserRole.Student, "Outsider");
        var session = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList());

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var response = await client.PostAsJsonAsync(
            $"/sessions/{session.Id}/unblock",
            new UnblockGrantRequest(outsider.Id, "reddit.com"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a host")]
    [InlineData("https://reddit.com")]
    [InlineData("reddit.com/path")]
    public async Task POST_unblock_with_invalid_host_returns_400(string host)
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var session = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList());

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var response = await client.PostAsJsonAsync(
            $"/sessions/{session.Id}/unblock",
            new UnblockGrantRequest(scenario.Students[0].Id, host));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task POST_unblock_happy_path_persists_grant_and_broadcasts()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var session = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList());
        var student = scenario.Students[0];

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var response = await client.PostAsJsonAsync(
            $"/sessions/{session.Id}/unblock",
            new UnblockGrantRequest(student.Id, "REDDIT.com"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<UnblockGrantResponse>();
        Assert.NotNull(body);
        Assert.Equal(session.Id, body!.SessionId);
        Assert.Equal(student.Id, body.UserId);
        Assert.Equal("reddit.com", body.Host);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        var grant = await db.SessionUnblockGrants.AsNoTracking()
            .SingleAsync(g => g.SessionId == session.Id && g.UserId == student.Id);
        Assert.Equal("reddit.com", grant.Host);

        var broadcaster = _factory.Services.GetRequiredService<RecordingSessionBroadcaster>();
        var broadcast = Assert.Single(broadcaster.AllowlistAmendedCalls,
            p => p.SessionId == session.Id && p.UserId == student.Id);
        var added = Assert.Single(broadcast.AddedDomains);
        Assert.Equal("reddit.com", added.Value);
        Assert.Equal(AllowedDomainMatchTypes.Suffix, added.MatchType);
    }

    [Fact]
    public async Task POST_unblock_is_idempotent_on_repeated_approval()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var session = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList());
        var student = scenario.Students[0];

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var first = await client.PostAsJsonAsync(
            $"/sessions/{session.Id}/unblock",
            new UnblockGrantRequest(student.Id, "reddit.com"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<UnblockGrantResponse>();

        // Second approval for the same (user, host) should not duplicate the
        // grant row but should still broadcast (a re-approve is a valid teacher
        // action — e.g. the student's extension lost the original push).
        var second = await client.PostAsJsonAsync(
            $"/sessions/{session.Id}/unblock",
            new UnblockGrantRequest(student.Id, "reddit.com"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondBody = await second.Content.ReadFromJsonAsync<UnblockGrantResponse>();
        Assert.Equal(firstBody!.GrantedAt, secondBody!.GrantedAt);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        var count = await db.SessionUnblockGrants.CountAsync(
            g => g.SessionId == session.Id && g.UserId == student.Id && g.Host == "reddit.com");
        Assert.Equal(1, count);

        var broadcaster = _factory.Services.GetRequiredService<RecordingSessionBroadcaster>();
        var broadcasts = broadcaster.AllowlistAmendedCalls
            .Where(p => p.SessionId == session.Id && p.UserId == student.Id)
            .ToList();
        Assert.Equal(2, broadcasts.Count);
    }

    [Fact]
    public async Task POST_unblock_per_student_records_a_student_scope_audit_event()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var session = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList());
        var student = scenario.Students[0];

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var response = await client.PostAsJsonAsync(
            $"/sessions/{session.Id}/unblock",
            new UnblockGrantRequest(student.Id, "reddit.com", Scope: null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<UnblockGrantResponse>();
        Assert.Equal("student", body!.Scope);
        Assert.Equal(student.Id, body.UserId);

        var approval = Assert.Single(await ReadUnblockApprovedEventsAsync(session.Id));
        Assert.Equal(student.Id, approval.UserId);
        Assert.Equal("reddit.com", approval.Host);
        Assert.Equal("student", approval.Scope);
    }

    // ------- POST /sessions/{id}/unblock (scope = class) (#101) -------

    [Fact]
    public async Task POST_unblock_whole_class_persists_session_wide_grant_and_broadcasts_to_active_participants()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory, studentCount: 3);
        var session = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList());
        // Two students are actively joined; the third never confirmed and so
        // shouldn't receive a live push (they'll pick the grant up on join).
        await MarkParticipantJoinedAsync(session.Id, scenario.Students[0].Id);
        await MarkParticipantJoinedAsync(session.Id, scenario.Students[1].Id);

        var broadcaster = _factory.Services.GetRequiredService<RecordingSessionBroadcaster>();
        broadcaster.AllowlistAmendedCalls.Clear();

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var response = await client.PostAsJsonAsync(
            $"/sessions/{session.Id}/unblock",
            new UnblockGrantRequest(UserId: null, Host: "REDDIT.com", Scope: "class"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<UnblockGrantResponse>();
        Assert.NotNull(body);
        Assert.Equal(session.Id, body!.SessionId);
        Assert.Null(body.UserId);
        Assert.Equal("reddit.com", body.Host);
        Assert.Equal("class", body.Scope);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
            var grant = await db.SessionWideUnblockGrants.AsNoTracking()
                .SingleAsync(g => g.SessionId == session.Id);
            Assert.Equal("reddit.com", grant.Host);
            // No per-student rows are written for a whole-class grant.
            Assert.Equal(0, await db.SessionUnblockGrants.CountAsync(g => g.SessionId == session.Id));
        }

        // One amendment per actively-joined participant, each suffix-matching the host.
        var broadcasts = broadcaster.AllowlistAmendedCalls
            .Where(p => p.SessionId == session.Id)
            .ToList();
        Assert.Equal(2, broadcasts.Count);
        Assert.Contains(broadcasts, p => p.UserId == scenario.Students[0].Id);
        Assert.Contains(broadcasts, p => p.UserId == scenario.Students[1].Id);
        Assert.DoesNotContain(broadcasts, p => p.UserId == scenario.Students[2].Id);
        Assert.All(broadcasts, p =>
        {
            var added = Assert.Single(p.AddedDomains);
            Assert.Equal("reddit.com", added.Value);
            Assert.Equal(AllowedDomainMatchTypes.Suffix, added.MatchType);
        });

        var approval = Assert.Single(await ReadUnblockApprovedEventsAsync(session.Id));
        Assert.Equal("reddit.com", approval.Host);
        Assert.Equal("class", approval.Scope);
        // A whole-class grant has no single student subject; it's attributed to
        // the teacher so the audit row still has an actor.
        Assert.Equal(scenario.Teacher.Id, approval.UserId);
    }

    [Fact]
    public async Task POST_unblock_whole_class_is_idempotent_on_repeated_approval()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var session = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList());

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var first = await client.PostAsJsonAsync(
            $"/sessions/{session.Id}/unblock",
            new UnblockGrantRequest(UserId: null, Host: "reddit.com", Scope: "class"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<UnblockGrantResponse>();

        var second = await client.PostAsJsonAsync(
            $"/sessions/{session.Id}/unblock",
            new UnblockGrantRequest(UserId: null, Host: "reddit.com", Scope: "class"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondBody = await second.Content.ReadFromJsonAsync<UnblockGrantResponse>();
        // GrantedAt is pinned to the first approval — a re-approve doesn't reset it.
        Assert.Equal(firstBody!.GrantedAt, secondBody!.GrantedAt);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        var count = await db.SessionWideUnblockGrants.CountAsync(
            g => g.SessionId == session.Id && g.Host == "reddit.com");
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task POST_unblock_whole_class_for_ended_session_returns_400()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var session = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList(), ended: true);

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var response = await client.PostAsJsonAsync(
            $"/sessions/{session.Id}/unblock",
            new UnblockGrantRequest(UserId: null, Host: "reddit.com", Scope: "class"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ------- GET /sessions/{id}/unblock-requests -------

    [Fact]
    public async Task GET_unblock_requests_unauthenticated_returns_401()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var session = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList());

        using var client = _factory.CreateClient();
        var response = await client.GetAsync($"/sessions/{session.Id}/unblock-requests");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GET_unblock_requests_as_student_returns_403()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var session = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList());

        using var client = _factory.CreateClient();
        TestAuth.SetStudent(client, scenario.Students[0]);

        var response = await client.GetAsync($"/sessions/{session.Id}/unblock-requests");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GET_unblock_requests_by_non_owning_teacher_returns_403()
    {
        var owned = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var other = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var session = await TestSeed.AddSessionAsync(
            _factory, owned.Teacher.Id, owned.Class.Id, owned.Students.Select(s => s.Id).ToList());

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, other.Teacher);

        var response = await client.GetAsync($"/sessions/{session.Id}/unblock-requests");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GET_unblock_requests_returns_empty_when_no_events()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var session = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList());

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var body = await client.GetFromJsonAsync<List<UnblockRequestSummary>>(
            $"/sessions/{session.Id}/unblock-requests");

        Assert.NotNull(body);
        Assert.Empty(body!);
    }

    [Fact]
    public async Task GET_unblock_requests_groups_by_host_with_count_of_distinct_students()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory, studentCount: 3);
        var session = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList());

        await SeedUnblockEventAsync(session.Id, scenario.Students[0].Id, "reddit.com", "https://reddit.com/r/foo");
        await SeedUnblockEventAsync(session.Id, scenario.Students[1].Id, "reddit.com", "https://reddit.com/r/bar");
        // Student[0] clicks again on the same host — should collapse into one
        // requester row, the count for reddit.com is still 2 distinct students.
        await SeedUnblockEventAsync(session.Id, scenario.Students[0].Id, "reddit.com", "https://reddit.com/r/baz");
        await SeedUnblockEventAsync(session.Id, scenario.Students[2].Id, "imgur.com", "https://imgur.com/gallery");

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var body = await client.GetFromJsonAsync<List<UnblockRequestSummary>>(
            $"/sessions/{session.Id}/unblock-requests");

        Assert.NotNull(body);
        Assert.Equal(2, body!.Count);

        var reddit = body.Single(s => s.Host == "reddit.com");
        Assert.Equal(2, reddit.Count);
        Assert.Equal(2, reddit.Requesters.Count);
        Assert.Contains(reddit.Requesters, r => r.UserId == scenario.Students[0].Id);
        Assert.Contains(reddit.Requesters, r => r.UserId == scenario.Students[1].Id);

        var imgur = body.Single(s => s.Host == "imgur.com");
        Assert.Equal(1, imgur.Count);
    }

    [Fact]
    public async Task GET_unblock_requests_omits_requests_already_granted()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory, studentCount: 2);
        var session = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList());

        await SeedUnblockEventAsync(session.Id, scenario.Students[0].Id, "reddit.com", "https://reddit.com/");
        await SeedUnblockEventAsync(session.Id, scenario.Students[1].Id, "reddit.com", "https://reddit.com/");

        // Approve only student[0] — student[1] should still appear as pending.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
            db.SessionUnblockGrants.Add(new SessionUnblockGrant
            {
                SessionId = session.Id,
                UserId = scenario.Students[0].Id,
                Host = "reddit.com",
                GrantedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var body = await client.GetFromJsonAsync<List<UnblockRequestSummary>>(
            $"/sessions/{session.Id}/unblock-requests");

        var reddit = Assert.Single(body!);
        Assert.Equal("reddit.com", reddit.Host);
        Assert.Equal(1, reddit.Count);
        Assert.Equal(scenario.Students[1].Id, reddit.Requesters.Single().UserId);
    }

    [Fact]
    public async Task GET_unblock_requests_omits_hosts_granted_whole_class_for_every_student()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory, studentCount: 2);
        var session = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList());

        // Both students requested reddit.com, and one also requested imgur.com.
        await SeedUnblockEventAsync(session.Id, scenario.Students[0].Id, "reddit.com", "https://reddit.com/");
        await SeedUnblockEventAsync(session.Id, scenario.Students[1].Id, "reddit.com", "https://reddit.com/");
        await SeedUnblockEventAsync(session.Id, scenario.Students[1].Id, "imgur.com", "https://imgur.com/");

        // A whole-class grant for reddit.com covers everyone — it should drop
        // off the pending list for all students, even the ones who asked.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
            db.SessionWideUnblockGrants.Add(new Anchor.Domain.Sessions.SessionWideUnblockGrant
            {
                SessionId = session.Id,
                Host = "reddit.com",
                GrantedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var body = await client.GetFromJsonAsync<List<UnblockRequestSummary>>(
            $"/sessions/{session.Id}/unblock-requests");

        // Only imgur.com remains pending; reddit.com is gone entirely.
        var imgur = Assert.Single(body!);
        Assert.Equal("imgur.com", imgur.Host);
    }

    private async Task MarkParticipantJoinedAsync(Guid sessionId, Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        var participant = await db.SessionParticipants
            .SingleAsync(p => p.SessionId == sessionId && p.UserId == userId);
        participant.JoinedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
    }

    private async Task<IReadOnlyList<(Guid UserId, string Host, string Scope)>> ReadUnblockApprovedEventsAsync(Guid sessionId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        var rows = await db.Events.AsNoTracking()
            .Where(e => e.SessionId == sessionId && e.Kind == EventKind.UnblockApproved)
            .Select(e => new { e.UserId, e.PayloadJson })
            .ToListAsync();

        return rows.Select(r =>
        {
            using var doc = System.Text.Json.JsonDocument.Parse(r.PayloadJson);
            var root = doc.RootElement;
            var host = root.GetProperty("host").GetString()!;
            var scope = root.GetProperty("scope").GetString()!;
            return (r.UserId, host, scope);
        }).ToList();
    }

    private async Task SeedUnblockEventAsync(Guid sessionId, Guid userId, string host, string url)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        var payload = System.Text.Json.JsonSerializer.Serialize(new { url, host });
        db.Events.Add(new Event
        {
            SessionId = sessionId,
            UserId = userId,
            Kind = EventKind.UnblockRequest,
            PayloadJson = payload,
            OccurredAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}
