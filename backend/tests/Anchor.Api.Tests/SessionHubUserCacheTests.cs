using Anchor.Api.Realtime;
using Anchor.Api.Tests.FakeAuth;
using Anchor.Domain.Classes;
using Anchor.Domain.Sessions;
using Anchor.Domain.Users;
using Anchor.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Anchor.Api.Tests;

/// <summary>
/// Asserts that the resolved <see cref="User"/> is cached on the SignalR
/// connection for its lifetime so that repeated hub invocations don't each
/// re-issue <c>SELECT Users WHERE EntraOid</c>. See issue #55.
/// </summary>
public sealed class SessionHubUserCacheTests : IClassFixture<SessionHubUserCacheTests.CountingFactory>
{
    private readonly CountingFactory _factory;

    public SessionHubUserCacheTests(CountingFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task User_resolved_once_per_connection_across_multiple_hub_invocations()
    {
        var (student, session) = await SeedSessionWithStudentAsync();
        var counter = _factory.Services.GetRequiredService<UserStoreCallCounter>();
        counter.Reset();

        await using var connection = BuildConnection(student.EntraOid);
        await connection.StartAsync();

        await connection.InvokeAsync<JoinSessionResult>(
            "JoinSession",
            new JoinSessionRequest(session.Id, JoinCode: null));
        await connection.InvokeAsync("Heartbeat", session.Id);
        await connection.InvokeAsync("Heartbeat", session.Id);
        await connection.InvokeAsync("LeaveSession", session.Id);

        Assert.Equal(1, counter.FindByEntraOidCount);
    }

    private async Task<(User student, Session session)> SeedSessionWithStudentAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();

        var teacher = new User
        {
            EntraOid = Guid.NewGuid(),
            DisplayName = "Cache Test Teacher",
            Role = UserRole.Teacher,
        };
        var student = new User
        {
            EntraOid = Guid.NewGuid(),
            DisplayName = "Cache Test Student",
            Role = UserRole.Student,
        };
        var @class = new Class
        {
            Name = $"CacheClass-{Guid.NewGuid():N}",
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

    private HubConnection BuildConnection(Guid oid)
    {
        var server = _factory.Server;
        return new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, SessionHub.Path.TrimStart('/')), options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.Headers[FakeJwtBearerHandler.HeaderOid] = oid.ToString();
                options.Headers[FakeJwtBearerHandler.HeaderRole] = "Student";
                options.Headers[FakeJwtBearerHandler.HeaderName] = "Cache Test User";
            })
            .Build();
    }

    public sealed class UserStoreCallCounter
    {
        private int _findByEntraOidCount;

        public int FindByEntraOidCount => Volatile.Read(ref _findByEntraOidCount);

        public void Increment() => Interlocked.Increment(ref _findByEntraOidCount);

        public void Reset() => Interlocked.Exchange(ref _findByEntraOidCount, 0);
    }

    private sealed class CountingUserStore : IUserStore
    {
        private readonly AnchorDbContext _db;
        private readonly UserStoreCallCounter _counter;

        public CountingUserStore(AnchorDbContext db, UserStoreCallCounter counter)
        {
            _db = db;
            _counter = counter;
        }

        public Task<User?> FindByEntraOidAsync(Guid entraOid, CancellationToken cancellationToken = default)
        {
            _counter.Increment();
            return _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.EntraOid == entraOid, cancellationToken);
        }

        public Task<User> UpsertAsync(Guid entraOid, string displayName, UserRole role, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("UpsertAsync is not exercised by SessionHubUserCacheTests.");
    }

    public sealed class CountingFactory : AnchorApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<UserStoreCallCounter>();
                services.RemoveAll<IUserStore>();
                services.AddScoped<IUserStore, CountingUserStore>();
            });
        }
    }
}
