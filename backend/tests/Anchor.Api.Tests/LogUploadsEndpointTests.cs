using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Anchor.Api.Controllers;
using Anchor.Domain.Classes;
using Anchor.Domain.Sessions;
using Anchor.Domain.Users;
using Anchor.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Anchor.Api.Tests;

/// <summary>
/// The test phase's agent log uploads (#446), as the agent and an admin use
/// them: the backend decides each session's window (from 30 minutes before the
/// session started to 10 minutes after it ended), reports the windows it still
/// needs on every connect — those of sessions whose agent never joined
/// included — stores what the agent sends, takes nothing from a student for
/// someone else's session, caps the size, and only lets admins download. Off,
/// it reports nothing and takes nothing.
/// </summary>
public sealed class LogUploadsEndpointTests
    : IClassFixture<LogUploadsEndpointTests.OnFactory>, IClassFixture<LogUploadsEndpointTests.OffFactory>
{
    private const int MaxUploadBytes = 4096;
    private static readonly TimeSpan Before = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan After = TimeSpan.FromMinutes(10);

    private readonly OnFactory _on;
    private readonly OffFactory _off;

    public LogUploadsEndpointTests(OnFactory on, OffFactory off)
    {
        _on = on;
        _off = off;
    }

    /// <summary>Uploads switched on, with a small size cap and a fake clock.</summary>
    public sealed class OnFactory : AnchorApiFactory
    {
        public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LogUpload:Enabled"] = "true",
                ["LogUpload:MaxUploadBytes"] = MaxUploadBytes.ToString(),
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Clock);
            });
        }
    }

    /// <summary>The shipped defaults: uploads off.</summary>
    public sealed class OffFactory : AnchorApiFactory { }

    // ------- The switch -------

    [Fact]
    public async Task Off_by_default_no_windows_are_reported_and_no_upload_is_taken()
    {
        var (teacher, students, @class) = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_off, studentCount: 1);
        var student = students[0];
        var now = DateTimeOffset.UtcNow;
        var session = await AddSessionAsync(_off, teacher.Id, @class.Id, now.AddHours(-2), now.AddHours(-1), [student.Id]);

        var client = _off.CreateClient();
        TestAuth.SetStudent(client, student);

        var pending = await client.GetFromJsonAsync<PendingLogUploadsResponse>("/log-uploads/pending");
        Assert.NotNull(pending);
        Assert.Empty(pending!.Windows);

        using var upload = await UploadAsync(client, session.Id, now.AddMinutes(-50), Gzip("line"));
        Assert.Equal(HttpStatusCode.NotFound, upload.StatusCode);
        Assert.Equal(0, await CountUploadsAsync(_off, session.Id));
    }

    // ------- The windows -------

    [Fact]
    public async Task Every_roster_session_is_pending_with_its_window_including_one_the_agent_never_joined()
    {
        var (teacher, students, @class) = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_on, studentCount: 1);
        var student = students[0];
        var now = _on.Clock.GetUtcNow();
        // On the roster, but the agent never joined (no JoinedAt): exactly the
        // case the logs have to explain.
        var neverJoined = await AddSessionAsync(_on, teacher.Id, @class.Id, now.AddHours(-5), now.AddHours(-4), [student.Id]);
        var joined = await AddSessionAsync(_on, teacher.Id, @class.Id, now.AddHours(-3), now.AddHours(-2), [student.Id], joined: true);

        var pending = await PendingAsync(student);

        Assert.Equal(
            [
                new PendingLogWindow(neverJoined.Id, neverJoined.StartedAt - Before, neverJoined.EndedAt!.Value + After),
                new PendingLogWindow(joined.Id, joined.StartedAt - Before, joined.EndedAt!.Value + After),
            ],
            pending.Windows);
        Assert.Equal(MaxUploadBytes, pending.MaxBytes);
    }

    [Fact]
    public async Task An_open_window_is_reported_up_to_now()
    {
        var (teacher, students, @class) = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_on, studentCount: 1);
        var student = students[0];
        var now = _on.Clock.GetUtcNow();
        var running = await AddSessionAsync(_on, teacher.Id, @class.Id, now.AddMinutes(-20), endedAt: null, [student.Id], joined: true);
        // Ended five minutes ago: its window closes five minutes from now.
        var closing = await AddSessionAsync(_on, teacher.Id, @class.Id, now.AddMinutes(-60), now.AddMinutes(-5), [student.Id]);

        var pending = await PendingAsync(student);

        Assert.Equal(
            [
                new PendingLogWindow(closing.Id, closing.StartedAt - Before, now),
                new PendingLogWindow(running.Id, running.StartedAt - Before, now),
            ],
            pending.Windows);
    }

    [Fact]
    public async Task Other_students_sessions_and_sessions_older_than_the_lookback_are_not_asked_for()
    {
        var (teacher, students, @class) = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_on, studentCount: 2);
        var (student, other) = (students[0], students[1]);
        var now = _on.Clock.GetUtcNow();
        await AddSessionAsync(_on, teacher.Id, @class.Id, now.AddHours(-3), now.AddHours(-2), [other.Id]);
        await AddSessionAsync(_on, teacher.Id, @class.Id, now.AddDays(-15), now.AddDays(-15).AddHours(1), [student.Id]);
        var recent = await AddSessionAsync(_on, teacher.Id, @class.Id, now.AddDays(-13), now.AddDays(-13).AddHours(1), [student.Id]);

        var pending = await PendingAsync(student);

        Assert.Equal([recent.Id], pending.Windows.Select(w => w.SessionId));
    }

    // ------- Uploading -------

    [Fact]
    public async Task A_complete_upload_is_stored_as_sent_and_the_window_is_no_longer_asked_for()
    {
        var (teacher, students, @class) = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_on, studentCount: 1);
        var student = students[0];
        var now = _on.Clock.GetUtcNow();
        var session = await AddSessionAsync(_on, teacher.Id, @class.Id, now.AddHours(-3), now.AddHours(-2), [student.Id]);
        var window = (await PendingAsync(student)).Windows.Single(w => w.SessionId == session.Id);
        var body = Gzip("2026-10-21 10:00:00.000 +02:00 [INF] inside the window");

        var client = StudentClient(student);
        using (var upload = await UploadAsync(client, session.Id, window.To, body, agentVersion: "0.9.3"))
            Assert.Equal(HttpStatusCode.NoContent, upload.StatusCode);

        var stored = await SingleUploadAsync(session.Id);
        Assert.Equal(student.Id, stored.UserId);
        Assert.Equal(body, stored.Content);
        Assert.Equal(body.Length, stored.ContentLength);
        Assert.True(stored.IsComplete);
        Assert.Equal(window.From, stored.WindowFrom);
        Assert.Equal(window.To, stored.WindowTo);
        Assert.Equal("0.9.3", stored.AgentVersion);
        Assert.DoesNotContain((await PendingAsync(student)).Windows, w => w.SessionId == session.Id);

        // A second upload of a window the backend already has whole is refused.
        using var again = await UploadAsync(client, session.Id, window.To, Gzip("again"));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(body, (await SingleUploadAsync(session.Id)).Content);
    }

    [Fact]
    public async Task An_upload_of_an_open_window_stays_pending_and_the_complete_one_replaces_it()
    {
        var (teacher, students, @class) = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_on, studentCount: 1);
        var student = students[0];
        var startedAt = _on.Clock.GetUtcNow().AddMinutes(-20);
        var session = await AddSessionAsync(_on, teacher.Id, @class.Id, startedAt, endedAt: null, [student.Id], joined: true);
        var client = StudentClient(student);

        // Mid-session (an agent restarted by the updater, say): what there is so far.
        var open = (await PendingAsync(student)).Windows.Single(w => w.SessionId == session.Id);
        using (var partial = await UploadAsync(client, session.Id, open.To, Gzip("so far")))
            Assert.Equal(HttpStatusCode.NoContent, partial.StatusCode);
        Assert.False((await SingleUploadAsync(session.Id)).IsComplete);
        Assert.Contains((await PendingAsync(student)).Windows, w => w.SessionId == session.Id);

        // The session ends; once its window has closed, the next connect sends it whole.
        _on.Clock.Advance(TimeSpan.FromMinutes(5));
        var endedAt = _on.Clock.GetUtcNow();
        await EndSessionAsync(_on, session.Id, endedAt);
        _on.Clock.Advance(After);
        var closed = (await PendingAsync(student)).Windows.Single(w => w.SessionId == session.Id);
        Assert.Equal(endedAt + After, closed.To);
        var whole = Gzip("the whole window");
        using (var complete = await UploadAsync(client, session.Id, closed.To, whole))
            Assert.Equal(HttpStatusCode.NoContent, complete.StatusCode);

        var stored = await SingleUploadAsync(session.Id);
        Assert.True(stored.IsComplete);
        Assert.Equal(whole, stored.Content);
        Assert.Equal(closed.To, stored.WindowTo);
        Assert.DoesNotContain((await PendingAsync(student)).Windows, w => w.SessionId == session.Id);
    }

    [Fact]
    public async Task A_student_can_only_upload_for_a_session_they_were_on_the_roster_of()
    {
        var (teacher, students, @class) = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_on, studentCount: 2);
        var (owner, other) = (students[0], students[1]);
        var now = _on.Clock.GetUtcNow();
        var session = await AddSessionAsync(_on, teacher.Id, @class.Id, now.AddHours(-3), now.AddHours(-2), [owner.Id]);
        var window = (await PendingAsync(owner)).Windows.Single(w => w.SessionId == session.Id);

        using var foreign = await UploadAsync(StudentClient(other), session.Id, window.To, Gzip("not mine"));
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(0, await CountUploadsAsync(_on, session.Id));

        using var own = await UploadAsync(StudentClient(owner), session.Id, window.To, Gzip("mine"));
        Assert.Equal(HttpStatusCode.NoContent, own.StatusCode);
        Assert.Equal(owner.Id, (await SingleUploadAsync(session.Id)).UserId);
    }

    [Fact]
    public async Task The_cut_has_to_lie_inside_the_sessions_window()
    {
        var (teacher, students, @class) = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_on, studentCount: 1);
        var student = students[0];
        var now = _on.Clock.GetUtcNow();
        var session = await AddSessionAsync(_on, teacher.Id, @class.Id, now.AddHours(-3), now.AddHours(-2), [student.Id]);
        var window = (await PendingAsync(student)).Windows.Single(w => w.SessionId == session.Id);
        var client = StudentClient(student);

        using (var late = await UploadAsync(client, session.Id, window.To.AddSeconds(1), Gzip("x")))
            Assert.Equal(HttpStatusCode.BadRequest, late.StatusCode);
        using (var early = await UploadAsync(client, session.Id, window.From.AddSeconds(-1), Gzip("x")))
            Assert.Equal(HttpStatusCode.BadRequest, early.StatusCode);
        using (var missing = await client.PostAsync($"/log-uploads/{session.Id}", GzipContent(Gzip("x"))))
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(0, await CountUploadsAsync(_on, session.Id));
    }

    [Fact]
    public async Task An_upload_over_the_size_cap_or_not_gzipped_is_refused()
    {
        var (teacher, students, @class) = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_on, studentCount: 1);
        var student = students[0];
        var now = _on.Clock.GetUtcNow();
        var session = await AddSessionAsync(_on, teacher.Id, @class.Id, now.AddHours(-3), now.AddHours(-2), [student.Id]);
        var window = (await PendingAsync(student)).Windows.Single(w => w.SessionId == session.Id);
        var client = StudentClient(student);
        var tooBig = new byte[MaxUploadBytes + 1];
        tooBig[0] = 0x1f;
        tooBig[1] = 0x8b;

        using (var declared = await UploadAsync(client, session.Id, window.To, tooBig))
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, declared.StatusCode);
        // Without a Content-Length (chunked), the backend stops reading at the cap.
        using (var chunked = await client.PostAsync(
                   UploadUrl(session.Id, window.To, agentVersion: null), new UnsizedContent(tooBig)))
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, chunked.StatusCode);
        using (var plain = await UploadAsync(client, session.Id, window.To, Encoding.UTF8.GetBytes("plain text")))
            Assert.Equal(HttpStatusCode.BadRequest, plain.StatusCode);
        Assert.Equal(0, await CountUploadsAsync(_on, session.Id));

        var atTheCap = new byte[MaxUploadBytes];
        atTheCap[0] = 0x1f;
        atTheCap[1] = 0x8b;
        using var fits = await UploadAsync(client, session.Id, window.To, atTheCap);
        Assert.Equal(HttpStatusCode.NoContent, fits.StatusCode);
    }

    // ------- Admin access -------

    [Fact]
    public async Task Only_admins_can_list_and_download_an_upload()
    {
        var (teacher, students, @class) = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_on, studentCount: 1);
        var student = students[0];
        var admin = await TestSeed.AddUserAsync(_on, UserRole.Admin, "Admin " + Guid.NewGuid().ToString("N")[..6]);
        var now = _on.Clock.GetUtcNow();
        var session = await AddSessionAsync(_on, teacher.Id, @class.Id, now.AddHours(-3), now.AddHours(-2), [student.Id]);
        var window = (await PendingAsync(student)).Windows.Single(w => w.SessionId == session.Id);
        var body = Gzip("the student's lines");
        using (var upload = await UploadAsync(StudentClient(student), session.Id, window.To, body, agentVersion: "0.9.3"))
            Assert.Equal(HttpStatusCode.NoContent, upload.StatusCode);
        var download = $"/admin/log-uploads/{session.Id}/{student.Id}";
        var list = $"/admin/log-uploads?sessionId={session.Id}";

        var adminClient = _on.CreateClient();
        TestAuth.SetAdmin(adminClient, admin);
        using (var res = await adminClient.GetAsync(download))
        {
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal("application/gzip", res.Content.Headers.ContentType?.MediaType);
            Assert.Equal(body, await res.Content.ReadAsByteArrayAsync());
        }
        var summaries = await adminClient.GetFromJsonAsync<List<LogUploadSummary>>(list);
        var summary = Assert.Single(summaries!);
        Assert.Equal(
            new LogUploadSummary(session.Id, student.Id, student.DisplayName, window.From, window.To,
                IsComplete: true, now, "0.9.3", body.Length),
            summary);
        using (var unknown = await adminClient.GetAsync($"/admin/log-uploads/{session.Id}/{Guid.NewGuid()}"))
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var teacherClient = _on.CreateClient();
        TestAuth.SetTeacher(teacherClient, teacher);
        var studentClient = StudentClient(student);
        foreach (var client in new[] { teacherClient, studentClient })
        {
            using (var res = await client.GetAsync(download))
                Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
            using (var res = await client.GetAsync(list))
                Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        }
    }

    // ------- Helpers -------

    private HttpClient StudentClient(User student)
    {
        var client = _on.CreateClient();
        TestAuth.SetStudent(client, student);
        return client;
    }

    private async Task<PendingLogUploadsResponse> PendingAsync(User student)
    {
        var pending = await StudentClient(student).GetFromJsonAsync<PendingLogUploadsResponse>("/log-uploads/pending");
        return pending!;
    }

    private static string UploadUrl(Guid sessionId, DateTimeOffset to, string? agentVersion) =>
        $"/log-uploads/{sessionId}?to={Uri.EscapeDataString(to.ToString("O"))}"
        + (agentVersion is null ? "" : $"&agentVersion={Uri.EscapeDataString(agentVersion)}");

    private static Task<HttpResponseMessage> UploadAsync(
        HttpClient client, Guid sessionId, DateTimeOffset to, byte[] body, string? agentVersion = null) =>
        client.PostAsync(UploadUrl(sessionId, to, agentVersion), GzipContent(body));

    private static ByteArrayContent GzipContent(byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");
        return content;
    }

    private static byte[] Gzip(string text)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal))
            gzip.Write(Encoding.UTF8.GetBytes(text));
        return buffer.ToArray();
    }

    private static async Task<Session> AddSessionAsync(
        AnchorApiFactory factory,
        Guid teacherId,
        Guid classId,
        DateTimeOffset startedAt,
        DateTimeOffset? endedAt,
        IReadOnlyCollection<Guid> participantIds,
        bool joined = false)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        var session = new Session
        {
            TeacherId = teacherId,
            ClassId = classId,
            StartedAt = startedAt,
            EndedAt = endedAt,
            JoinCode = Random.Shared.Next(0, 1_000_000).ToString("D6"),
        };
        db.Sessions.Add(session);
        foreach (var userId in participantIds)
        {
            db.SessionParticipants.Add(new SessionParticipant
            {
                SessionId = session.Id,
                UserId = userId,
                JoinedAt = joined ? startedAt.AddSeconds(5) : null,
            });
        }
        await db.SaveChangesAsync();
        return session;
    }

    private static async Task EndSessionAsync(AnchorApiFactory factory, Guid sessionId, DateTimeOffset endedAt)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        var session = await db.Sessions.SingleAsync(s => s.Id == sessionId);
        session.EndedAt = endedAt;
        await db.SaveChangesAsync();
    }

    private static async Task<int> CountUploadsAsync(AnchorApiFactory factory, Guid sessionId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        return await db.SessionLogUploads.CountAsync(u => u.SessionId == sessionId);
    }

    private async Task<SessionLogUpload> SingleUploadAsync(Guid sessionId)
    {
        using var scope = _on.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        return await db.SessionLogUploads.AsNoTracking().SingleAsync(u => u.SessionId == sessionId);
    }

    /// <summary>A body sent without a Content-Length, as a chunked upload is.</summary>
    private sealed class UnsizedContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
