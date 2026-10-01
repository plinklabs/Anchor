using System.Data.Common;
using System.Net.Http.Json;
using Anchor.Api.Controllers;
using Anchor.Api.Realtime;
using Anchor.Api.Tests.FakeAuth;
using Anchor.Domain.Events;
using Anchor.Domain.Users;
using Anchor.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Anchor.Api.Tests;

/// <summary>
/// Heartbeat and event validation answered from memory (#342), driven through
/// real hub connections. Every agent and extension pings every 10 s, so a
/// database query per ping made heartbeat validation the largest share of
/// database load. These tests count the queries that actually reach
/// <c>SessionParticipants</c> and check that every way a participant becomes
/// active or inactive still decides which pings are accepted.
/// </summary>
public sealed class SessionHubActiveParticipantTests
    : IClassFixture<SessionHubActiveParticipantTests.CountingFactory>
{
    private const int Pings = 5;

    private readonly CountingFactory _factory;

    public SessionHubActiveParticipantTests(CountingFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Heartbeats_from_a_joined_participant_do_not_query_the_database()
    {
        var (student, session) = await SeedSessionWithStudentAsync();
        await using var connection = await ConnectAsync(student);
        await JoinAsync(connection, session.Id);

        _factory.ParticipantQueries.Reset();
        for (var i = 0; i < Pings; i++)
        {
            await connection.InvokeAsync("Heartbeat", session.Id);
            await connection.InvokeAsync("ExtensionHeartbeat", session.Id);
        }

        Assert.Equal(0, _factory.ParticipantQueries.Count);
        var tracker = _factory.Services.GetRequiredService<HeartbeatTracker>();
        Assert.True(tracker.TryGet(session.Id, student.Id, out _, WitnessSource.Agent));
        Assert.True(tracker.TryGet(session.Id, student.Id, out _, WitnessSource.Extension));
    }

    [Fact]
    public async Task Reported_events_from_a_joined_participant_do_not_query_participants()
    {
        var (student, session) = await SeedSessionWithStudentAsync();
        await using var connection = await ConnectAsync(student);
        await JoinAsync(connection, session.Id);

        _factory.ParticipantQueries.Reset();
        for (var i = 0; i < Pings; i++)
        {
            await connection.InvokeAsync("ReportEvent", new ReportEventRequest(
                session.Id, nameof(EventKind.ForegroundChange), "{\"app\":\"notepad\"}", OccurredAt: null));
        }

        Assert.Equal(0, _factory.ParticipantQueries.Count);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        Assert.Equal(Pings, await db.Events.CountAsync(e => e.SessionId == session.Id && e.UserId == student.Id));
    }

    [Fact]
    public async Task After_a_restart_heartbeats_fall_back_to_the_database_once_and_are_accepted()
    {
        // The participant joined before the backend restarted: the row says
        // joined, nothing is cached, and the agent keeps pinging on its
        // reconnected connection without calling JoinSession again.
        var (student, session) = await SeedSessionWithStudentAsync(joinedAt: DateTimeOffset.UtcNow.AddMinutes(-10));
        await using var connection = await ConnectAsync(student);

        _factory.ParticipantQueries.Reset();
        for (var i = 0; i < Pings; i++)
            await connection.InvokeAsync("Heartbeat", session.Id);

        Assert.Equal(1, _factory.ParticipantQueries.Count);
        var tracker = _factory.Services.GetRequiredService<HeartbeatTracker>();
        Assert.True(tracker.TryGet(session.Id, student.Id, out _));
    }

    [Fact]
    public async Task Heartbeats_from_a_non_participant_are_rejected_and_query_the_database_once()
    {
        var (_, session) = await SeedSessionWithStudentAsync();
        var stranger = await TestSeed.AddUserAsync(_factory, UserRole.Student, "Stranger");
        await using var connection = await ConnectAsync(stranger);

        _factory.ParticipantQueries.Reset();
        for (var i = 0; i < Pings; i++)
        {
            var ex = await Assert.ThrowsAsync<HubException>(() =>
                connection.InvokeAsync("ExtensionHeartbeat", session.Id));
            Assert.Contains("Not an active participant", ex.Message);
        }

        Assert.Equal(1, _factory.ParticipantQueries.Count);
        var tracker = _factory.Services.GetRequiredService<HeartbeatTracker>();
        Assert.False(tracker.TryGet(session.Id, stranger.Id, out _, WitnessSource.Extension));
    }

    [Fact]
    public async Task Heartbeat_after_LeaveSession_is_rejected()
    {
        var (student, session) = await SeedSessionWithStudentAsync();
        await using var connection = await ConnectAsync(student);
        await JoinAsync(connection, session.Id);
        await connection.InvokeAsync("Heartbeat", session.Id);

        await connection.InvokeAsync("LeaveSession", session.Id);

        var ex = await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("Heartbeat", session.Id));
        Assert.Contains("Not an active participant", ex.Message);
    }

    [Fact]
    public async Task Extension_heartbeat_after_AgentKilled_is_rejected()
    {
        // #110: the student quit the agent, the extension keeps running and
        // pinging — those pings must not keep the departed student tracked.
        var (student, session) = await SeedSessionWithStudentAsync();
        await using var connection = await ConnectAsync(student);
        await JoinAsync(connection, session.Id);
        await connection.InvokeAsync("ExtensionHeartbeat", session.Id);

        await connection.InvokeAsync("ReportEvent", new ReportEventRequest(
            session.Id, nameof(EventKind.AgentKilled), PayloadJson: "{}", OccurredAt: null));

        var ex = await Assert.ThrowsAsync<HubException>(() =>
            connection.InvokeAsync("ExtensionHeartbeat", session.Id));
        Assert.Contains("Not an active participant", ex.Message);
        var tracker = _factory.Services.GetRequiredService<HeartbeatTracker>();
        Assert.False(tracker.TryGet(session.Id, student.Id, out _, WitnessSource.Extension));
    }

    [Fact]
    public async Task Rejoining_through_JoinSession_after_leaving_accepts_heartbeats_again()
    {
        var (student, session) = await SeedSessionWithStudentAsync();
        await using var connection = await ConnectAsync(student);
        await JoinAsync(connection, session.Id);
        await connection.InvokeAsync("LeaveSession", session.Id);
        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("Heartbeat", session.Id));

        await JoinAsync(connection, session.Id);

        await connection.InvokeAsync("Heartbeat", session.Id);
        var tracker = _factory.Services.GetRequiredService<HeartbeatTracker>();
        Assert.True(tracker.TryGet(session.Id, student.Id, out _));
    }

    [Fact]
    public async Task Rejoining_by_code_after_leaving_accepts_heartbeats_again()
    {
        var (student, session) = await SeedSessionWithStudentAsync();
        await using var connection = await ConnectAsync(student);
        await JoinAsync(connection, session.Id);
        await connection.InvokeAsync("LeaveSession", session.Id);
        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("Heartbeat", session.Id));

        using var client = _factory.CreateClient();
        TestAuth.SetStudent(client, student);
        var response = await client.PostAsJsonAsync("/sessions/join-by-code", new JoinByCodeRequest(session.JoinCode));
        response.EnsureSuccessStatusCode();

        await connection.InvokeAsync("Heartbeat", session.Id);
        var tracker = _factory.Services.GetRequiredService<HeartbeatTracker>();
        Assert.True(tracker.TryGet(session.Id, student.Id, out _));
    }

    [Fact]
    public async Task Ending_a_session_drops_its_cached_participants()
    {
        var (student, session) = await SeedSessionWithStudentAsync();
        await using var connection = await ConnectAsync(student);
        await JoinAsync(connection, session.Id);
        await connection.InvokeAsync("Heartbeat", session.Id);
        var cache = _factory.Services.GetRequiredService<ActiveParticipantCache>();
        Assert.True(cache.TryGet(session.Id, student.Id, out _));

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, session.Teacher);
        var response = await client.PostAsync($"/sessions/{session.Id}/end", content: null);
        response.EnsureSuccessStatusCode();

        Assert.False(cache.TryGet(session.Id, student.Id, out _));
    }

    private async Task<HubConnection> ConnectAsync(User user)
    {
        var server = _factory.Server;
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, SessionHub.Path.TrimStart('/')), options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.Headers[FakeJwtBearerHandler.HeaderOid] = user.EntraOid.ToString();
                options.Headers[FakeJwtBearerHandler.HeaderRole] = "Student";
                options.Headers[FakeJwtBearerHandler.HeaderName] = user.DisplayName;
            })
            .Build();
        await connection.StartAsync();
        return connection;
    }

    private static Task<JoinSessionResult> JoinAsync(HubConnection connection, Guid sessionId) =>
        connection.InvokeAsync<JoinSessionResult>("JoinSession", new JoinSessionRequest(sessionId, JoinCode: null));

    private async Task<(User Student, SeededSession Session)> SeedSessionWithStudentAsync(DateTimeOffset? joinedAt = null)
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory, studentCount: 1);
        var student = scenario.Students[0];
        var session = await TestSeed.AddSessionAsync(_factory, scenario.Teacher.Id, scenario.Class.Id, new[] { student.Id });

        if (joinedAt is not null)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
            var participant = await db.SessionParticipants.SingleAsync(
                p => p.SessionId == session.Id && p.UserId == student.Id);
            participant.JoinedAt = joinedAt;
            await db.SaveChangesAsync();
        }

        return (student, new SeededSession(session.Id, session.JoinCode, scenario.Teacher));
    }

    private sealed record SeededSession(Guid Id, string JoinCode, User Teacher);

    /// <summary>Counts the database commands that read <c>SessionParticipants</c>.</summary>
    public sealed class ParticipantQueryCounter : DbCommandInterceptor
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Reset() => Interlocked.Exchange(ref _count, 0);

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            CountIfParticipantRead(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            CountIfParticipantRead(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            CountIfParticipantRead(command);
            return result;
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            CountIfParticipantRead(command);
            return ValueTask.FromResult(result);
        }

        private void CountIfParticipantRead(DbCommand command)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("\"SessionParticipants\"", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _count);
            }
        }
    }

    /// <summary>
    /// Own factory (own database and singletons) with the query counter wired
    /// into every <see cref="AnchorDbContext"/>.
    /// </summary>
    public sealed class CountingFactory : AnchorApiFactory
    {
        public ParticipantQueryCounter ParticipantQueries { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
                services.ConfigureDbContext<AnchorDbContext>(options => options.AddInterceptors(ParticipantQueries)));
        }
    }
}
