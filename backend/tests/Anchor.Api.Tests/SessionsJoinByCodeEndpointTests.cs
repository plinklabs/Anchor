using System.Net;
using System.Net.Http.Json;
using Anchor.Api.Controllers;
using Anchor.Api.Sessions;
using Anchor.Api.Tests.FakeAuth;
using Anchor.Domain.Sessions;
using Anchor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Anchor.Api.Tests;

public sealed class SessionsJoinByCodeEndpointTests : IClassFixture<AnchorApiFactory>
{
    private readonly AnchorApiFactory _factory;

    public SessionsJoinByCodeEndpointTests(AnchorApiFactory factory)
    {
        _factory = factory;
        // Each test starts with a clean limiter bucket: the limiter is a
        // singleton across tests in the shared factory, and we don't want
        // earlier test failures to leak into the rate-limit test below.
        _factory.Services.GetRequiredService<JoinByCodeRateLimiter>();
    }

    [Fact]
    public async Task POST_join_by_code_unauthenticated_returns_401()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/sessions/join-by-code", new JoinByCodeRequest("123456"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12345A")]
    [InlineData("abcdef")]
    public async Task POST_join_by_code_with_malformed_code_returns_400(string code)
    {
        var outsider = await TestSeed.AddUserAsync(_factory, Anchor.Domain.Users.UserRole.Student, "Outsider " + Guid.NewGuid());

        using var client = _factory.CreateClient();
        TestAuth.SetStudent(client, outsider);

        var response = await client.PostAsJsonAsync("/sessions/join-by-code", new JoinByCodeRequest(code));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task POST_join_by_code_for_unknown_code_returns_404()
    {
        var outsider = await TestSeed.AddUserAsync(_factory, Anchor.Domain.Users.UserRole.Student, "Outsider " + Guid.NewGuid());

        using var client = _factory.CreateClient();
        TestAuth.SetStudent(client, outsider);

        var response = await client.PostAsJsonAsync("/sessions/join-by-code", new JoinByCodeRequest("000000"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JoinByCodeErrorResponse>();
        Assert.NotNull(error);
        Assert.False(string.IsNullOrWhiteSpace(error!.Message));
    }

    [Fact]
    public async Task POST_join_by_code_for_ended_session_returns_410()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var session = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id,
            scenario.Students.Select(s => s.Id).ToList(), ended: true);
        var outsider = await TestSeed.AddUserAsync(_factory, Anchor.Domain.Users.UserRole.Student, "Outsider " + Guid.NewGuid());

        using var client = _factory.CreateClient();
        TestAuth.SetStudent(client, outsider);

        var response = await client.PostAsJsonAsync("/sessions/join-by-code", new JoinByCodeRequest(session.JoinCode));

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
    }

    [Fact]
    public async Task POST_join_by_code_for_stale_session_returns_410()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var outsider = await TestSeed.AddUserAsync(_factory, Anchor.Domain.Users.UserRole.Student, "Outsider " + Guid.NewGuid());

        // Forge a session whose StartedAt is past the freshness window but
        // EndedAt is still null — the soft-expiry path of the endpoint.
        string joinCode;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
            var stale = new Session
            {
                TeacherId = scenario.Teacher.Id,
                ClassId = scenario.Class.Id,
                StartedAt = DateTimeOffset.UtcNow - SessionsController.JoinCodeFreshnessWindow - TimeSpan.FromMinutes(5),
                JoinCode = Random.Shared.Next(0, 1_000_000).ToString("D6"),
            };
            db.Sessions.Add(stale);
            await db.SaveChangesAsync();
            joinCode = stale.JoinCode;
        }

        using var client = _factory.CreateClient();
        TestAuth.SetStudent(client, outsider);

        var response = await client.PostAsJsonAsync("/sessions/join-by-code", new JoinByCodeRequest(joinCode));

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
    }

    [Fact]
    public async Task POST_join_by_code_when_already_in_other_active_session_returns_409()
    {
        var scenarioA = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var scenarioB = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);

        // The student is actively joined to session A (different class than B's session).
        var student = await TestSeed.AddUserAsync(_factory, Anchor.Domain.Users.UserRole.Student, "Bystander " + Guid.NewGuid());
        var sessionA = await TestSeed.AddSessionAsync(
            _factory, scenarioA.Teacher.Id, scenarioA.Class.Id, new[] { student.Id });
        await SetParticipantJoinedAsync(sessionA.Id, student.Id);

        var sessionB = await TestSeed.AddSessionAsync(
            _factory, scenarioB.Teacher.Id, scenarioB.Class.Id, Array.Empty<Guid>());

        using var client = _factory.CreateClient();
        TestAuth.SetStudent(client, student);

        var response = await client.PostAsJsonAsync("/sessions/join-by-code", new JoinByCodeRequest(sessionB.JoinCode));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task POST_join_by_code_happy_path_joins_and_broadcasts_single_target()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var session = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList());

        // An outsider — not on the class roster, so the original SessionStart
        // broadcast skipped them. The manual code is their only way in.
        var outsider = await TestSeed.AddUserAsync(_factory, Anchor.Domain.Users.UserRole.Student, "Manual " + Guid.NewGuid());

        var broadcaster = _factory.Services.GetRequiredService<RecordingSessionBroadcaster>();
        broadcaster.SessionStartedCalls.Clear();

        using var client = _factory.CreateClient();
        TestAuth.SetStudent(client, outsider);

        var response = await client.PostAsJsonAsync("/sessions/join-by-code", new JoinByCodeRequest(session.JoinCode));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JoinByCodeResponse>();
        Assert.NotNull(body);
        Assert.Equal(session.Id, body!.SessionId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        var participant = await db.SessionParticipants.AsNoTracking()
            .SingleAsync(p => p.SessionId == session.Id && p.UserId == outsider.Id);
        Assert.NotNull(participant);

        // The broadcast must be single-target: only the outsider gets it,
        // not the rostered students (who'd already received the original push).
        var call = Assert.Single(broadcaster.SessionStartedCalls, c => c.SessionId == session.Id);
        Assert.Equal(new[] { outsider.Id }, call.RecipientUserIds);
    }

    [Fact]
    public async Task POST_join_by_code_is_idempotent_on_repeat()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var session = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList());
        var outsider = await TestSeed.AddUserAsync(_factory, Anchor.Domain.Users.UserRole.Student, "Repeater " + Guid.NewGuid());

        using var client = _factory.CreateClient();
        TestAuth.SetStudent(client, outsider);

        var first = await client.PostAsJsonAsync("/sessions/join-by-code", new JoinByCodeRequest(session.JoinCode));
        var second = await client.PostAsJsonAsync("/sessions/join-by-code", new JoinByCodeRequest(session.JoinCode));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        var rows = await db.SessionParticipants.AsNoTracking()
            .Where(p => p.SessionId == session.Id && p.UserId == outsider.Id)
            .ToListAsync();
        Assert.Single(rows);
    }

    [Fact]
    public async Task POST_join_by_code_returns_429_after_max_failed_attempts()
    {
        var limiter = _factory.Services.GetRequiredService<JoinByCodeRateLimiter>();
        var student = await TestSeed.AddUserAsync(_factory, Anchor.Domain.Users.UserRole.Student, "Spammer " + Guid.NewGuid());
        limiter.Reset(student.Id);

        using var client = _factory.CreateClient();
        TestAuth.SetStudent(client, student);

        // Drive the first MaxFailedAttemptsPerWindow attempts to failure
        // (any non-existent code works).
        for (var i = 0; i < JoinByCodeRateLimiter.MaxFailedAttemptsPerWindow; i++)
        {
            var res = await client.PostAsJsonAsync("/sessions/join-by-code", new JoinByCodeRequest("000000"));
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        }

        // The next attempt — even with a valid code — must be rate-limited.
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var session = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, Array.Empty<Guid>());
        var blocked = await client.PostAsJsonAsync("/sessions/join-by-code", new JoinByCodeRequest(session.JoinCode));

        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
    }

    [Fact]
    public async Task POST_join_by_code_successful_join_resets_the_limiter()
    {
        var limiter = _factory.Services.GetRequiredService<JoinByCodeRateLimiter>();
        var student = await TestSeed.AddUserAsync(_factory, Anchor.Domain.Users.UserRole.Student, "Resetter " + Guid.NewGuid());
        limiter.Reset(student.Id);

        using var client = _factory.CreateClient();
        TestAuth.SetStudent(client, student);

        // Burn one failure, then succeed — the bucket should drain.
        var miss = await client.PostAsJsonAsync("/sessions/join-by-code", new JoinByCodeRequest("000000"));
        Assert.Equal(HttpStatusCode.NotFound, miss.StatusCode);

        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var session = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, Array.Empty<Guid>());
        var hit = await client.PostAsJsonAsync("/sessions/join-by-code", new JoinByCodeRequest(session.JoinCode));
        Assert.Equal(HttpStatusCode.OK, hit.StatusCode);

        Assert.False(limiter.IsBlocked(student.Id));
    }

    private async Task SetParticipantJoinedAsync(Guid sessionId, Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        var participant = await db.SessionParticipants
            .SingleAsync(p => p.SessionId == sessionId && p.UserId == userId);
        participant.JoinedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
    }
}
