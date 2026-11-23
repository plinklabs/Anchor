using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Anchor.Api.Controllers;
using Anchor.Domain.Classes;
using Anchor.Domain.Sessions;
using Anchor.Domain.Users;
using Anchor.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Anchor.Api.Tests;

/// <summary>
/// #566: an admin downloads the agents' log uploads only for a stated purpose
/// and reason, and, for a fraud suspicion, with who in the school's leadership
/// asked. Every download writes a <see cref="LogAccess"/> record per student
/// before any log content leaves the backend; one that can't be recorded
/// returns no log. One student downloads as <c>.txt.gz</c>, several of one
/// session as one <c>.zip</c>. The records are listed for review and can't be
/// changed or deleted through the API. Teachers and students get 403
/// everywhere.
/// </summary>
public sealed class AdminLogDownloadTests
    : IClassFixture<AdminLogDownloadTests.Factory>, IClassFixture<AdminLogDownloadTests.FailingRecordFactory>
{
    private const string Download = "/admin/log-uploads/download";
    private const string History = "/admin/log-access";
    private const string Reason = "The agent never joined this session.";

    private readonly Factory _factory;
    private readonly FailingRecordFactory _failing;

    public AdminLogDownloadTests(Factory factory, FailingRecordFactory failing)
    {
        _factory = factory;
        _failing = failing;
    }

    /// <summary>Own database and a fake clock.</summary>
    public class Factory : AnchorApiFactory
    {
        public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.Zero));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Clock);
            });
        }
    }

    /// <summary>A database that refuses to store an access record.</summary>
    public sealed class FailingRecordFactory : Factory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
                services.ConfigureDbContext<AnchorDbContext>(o => o.AddInterceptors(new RefuseLogAccess())));
        }
    }

    // ------- The record -------

    [Fact]
    public async Task A_download_records_who_when_which_student_and_why_before_returning_the_log()
    {
        var (admin, session, @class, students, contents) = await SeedAsync(_factory, studentCount: 1);
        var student = students[0];

        using var res = await AdminClient(_factory, admin).PostAsJsonAsync(Download, new DownloadLogUploadsRequest(
            session.Id, [student.Id], LogAccessPurpose.FraudSuspicion, "  Suspected use of a chat site during the test.  ",
            "  The principal  "));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/gzip", res.Content.Headers.ContentType?.MediaType);
        Assert.Equal(AdminLogUploadsController.SingleFileName(session.Id, student.Id), FileName(res));
        Assert.Equal(contents[student.Id], await res.Content.ReadAsByteArrayAsync());

        var record = Assert.Single(await RecordsAsync(_factory, session.Id));
        Assert.Equal(_factory.Clock.GetUtcNow(), record.AccessedAt);
        Assert.Equal(admin.Id, record.AdminUserId);
        Assert.Equal(admin.DisplayName, record.AdminDisplayName);
        Assert.Equal(session.StartedAt, record.SessionStartedAt);
        Assert.Equal(@class.Name, record.ClassName);
        Assert.Equal(student.Id, record.StudentUserId);
        Assert.Equal(student.DisplayName, record.StudentDisplayName);
        Assert.Equal(LogAccessPurpose.FraudSuspicion, record.Purpose);
        Assert.Equal("Suspected use of a chat site during the test.", record.Reason);
        Assert.Equal("The principal", record.RequestedBy);
    }

    [Fact]
    public async Task A_download_whose_record_cant_be_written_returns_no_log()
    {
        var (admin, session, _, students, _) = await SeedAsync(_failing, studentCount: 2);

        // The test server hands the client the exception a deployed backend
        // answers with a 500: no response at all, so no log.
        await Assert.ThrowsAsync<DbUpdateException>(() => AdminClient(_failing, admin).PostAsJsonAsync(
            Download, new DownloadLogUploadsRequest(
                session.Id, students.Select(s => s.Id).ToList(), LogAccessPurpose.TechnicalDiagnosis, Reason, null)));

        Assert.Empty(await RecordsAsync(_failing, session.Id));
    }

    [Fact]
    public async Task Several_students_of_one_session_download_as_one_zip_with_a_record_each()
    {
        var (admin, session, _, students, contents) = await SeedAsync(_factory, studentCount: 3);
        var picked = students.Take(2).ToList();

        using var res = await AdminClient(_factory, admin).PostAsJsonAsync(Download, new DownloadLogUploadsRequest(
            session.Id, picked.Select(s => s.Id).ToList(), LogAccessPurpose.TechnicalDiagnosis, Reason, null));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/zip", res.Content.Headers.ContentType?.MediaType);
        Assert.Equal(AdminLogUploadsController.ZipFileName(session.Id), FileName(res));
        using var zip = new ZipArchive(new MemoryStream(await res.Content.ReadAsByteArrayAsync()));
        Assert.Equal(2, zip.Entries.Count);
        foreach (var student in picked)
        {
            var entry = Assert.Single(zip.Entries, e => e.Name.EndsWith($"-{student.Id}.txt.gz", StringComparison.Ordinal));
            using var stream = entry.Open();
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            Assert.Equal(contents[student.Id], copy.ToArray());
        }

        var records = await RecordsAsync(_factory, session.Id);
        Assert.Equal(picked.Select(s => s.Id).Order(), records.Select(r => r.StudentUserId).Order());
        Assert.All(records, r => Assert.Null(r.RequestedBy));
    }

    // ------- Refused -------

    public static TheoryData<string, string> Incomplete => new()
    {
        // Missing outright: no purpose, no reason.
        { "no purpose", """{"sessionId":"{session}","userIds":["{student}"],"reason":"The agent never joined this session."}""" },
        { "no reason", """{"sessionId":"{session}","userIds":["{student}"],"purpose":"TechnicalDiagnosis"}""" },
        { "a blank reason", """{"sessionId":"{session}","userIds":["{student}"],"purpose":"TechnicalDiagnosis","reason":"   "}""" },
        { "a reason too short to say anything", """{"sessionId":"{session}","userIds":["{student}"],"purpose":"TechnicalDiagnosis","reason":"logs"}""" },
        { "a purpose the policy doesn't allow", """{"sessionId":"{session}","userIds":["{student}"],"purpose":"Curiosity","reason":"The agent never joined this session."}""" },
        { "a numeric purpose outside the two", """{"sessionId":"{session}","userIds":["{student}"],"purpose":7,"reason":"The agent never joined this session."}""" },
        { "a fraud suspicion without who asked", """{"sessionId":"{session}","userIds":["{student}"],"purpose":"FraudSuspicion","reason":"Suspected use of a chat site during the test."}""" },
        { "a fraud suspicion with a blank who asked", """{"sessionId":"{session}","userIds":["{student}"],"purpose":"FraudSuspicion","reason":"Suspected use of a chat site during the test.","requestedBy":"  "}""" },
        { "no students", """{"sessionId":"{session}","userIds":[],"purpose":"TechnicalDiagnosis","reason":"The agent never joined this session."}""" },
    };

    [Theory]
    [MemberData(nameof(Incomplete))]
    public async Task A_download_without_purpose_reason_or_who_asked_is_refused_and_returns_nothing(string what, string json)
    {
        var (admin, session, _, students, _) = await SeedAsync(_factory, studentCount: 1);
        var body = json.Replace("{session}", session.Id.ToString()).Replace("{student}", students[0].Id.ToString());

        using var res = await AdminClient(_factory, admin).PostAsync(
            Download, new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.True(res.StatusCode == HttpStatusCode.BadRequest, $"{what}: answered {(int)res.StatusCode}");
        Assert.False(IsGzip(await res.Content.ReadAsByteArrayAsync()), $"{what}: returned the log");
        Assert.Empty(await RecordsAsync(_factory, session.Id));
    }

    [Fact]
    public async Task A_download_with_a_student_without_an_upload_returns_nothing_and_records_nothing()
    {
        var (admin, session, _, students, _) = await SeedAsync(_factory, studentCount: 1);

        using var res = await AdminClient(_factory, admin).PostAsJsonAsync(Download, new DownloadLogUploadsRequest(
            session.Id, [students[0].Id, Guid.NewGuid()], LogAccessPurpose.TechnicalDiagnosis, Reason, null));

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Empty(await RecordsAsync(_factory, session.Id));
    }

    // ------- The history -------

    [Fact]
    public async Task The_history_lists_every_record_newest_first()
    {
        var (admin, session, @class, students, _) = await SeedAsync(_factory, studentCount: 1);
        var client = AdminClient(_factory, admin);
        var student = students[0];
        using (var first = await client.PostAsJsonAsync(Download, new DownloadLogUploadsRequest(
                   session.Id, [student.Id], LogAccessPurpose.TechnicalDiagnosis, Reason, null)))
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstAt = _factory.Clock.GetUtcNow();
        _factory.Clock.Advance(TimeSpan.FromMinutes(5));
        using (var second = await client.PostAsJsonAsync(Download, new DownloadLogUploadsRequest(
                   session.Id, [student.Id], LogAccessPurpose.FraudSuspicion, "Suspected use of a chat site during the test.", "The principal")))
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var history = (await client.GetFromJsonAsync<List<LogAccessRecord>>(History))!
            .Where(r => r.SessionId == session.Id)
            .ToList();

        Assert.Equal(2, history.Count);
        Assert.Equal(
            new LogAccessRecord(history[0].Id, _factory.Clock.GetUtcNow(), admin.Id, admin.DisplayName, session.Id,
                session.StartedAt, @class.Name, student.Id, student.DisplayName, LogAccessPurpose.FraudSuspicion,
                "Suspected use of a chat site during the test.", "The principal"),
            history[0]);
        Assert.Equal(firstAt, history[1].AccessedAt);
        Assert.Equal(LogAccessPurpose.TechnicalDiagnosis, history[1].Purpose);
    }

    [Fact]
    public void No_route_changes_or_deletes_a_record_and_none_returns_a_log_without_one()
    {
        var routes = _factory.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>()
            .ApiDescriptionGroups.Items
            .SelectMany(g => g.Items)
            .Select(a => $"{a.HttpMethod} {a.RelativePath}")
            .ToList();

        // The access history is read-only.
        Assert.Equal(["GET admin/log-access"], routes.Where(r => r.Contains("log-access", StringComparison.Ordinal)));
        // The admin side of the uploads: the listing and the recorded download.
        // The old reason-less GET admin/log-uploads/{sessionId}/{userId} is gone.
        Assert.Equal(
            ["GET admin/log-uploads", "POST admin/log-uploads/download"],
            routes.Where(r => r.Contains("admin/log-uploads", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
    }

    // ------- Admins only -------

    [Fact]
    public async Task Teachers_and_students_get_403_on_every_route()
    {
        var (_, session, _, students, _) = await SeedAsync(_factory, studentCount: 1);
        var teacher = await TestSeed.AddUserAsync(_factory, UserRole.Teacher, "Teacher " + Guid.NewGuid().ToString("N")[..6]);
        var request = new DownloadLogUploadsRequest(
            session.Id, [students[0].Id], LogAccessPurpose.TechnicalDiagnosis, Reason, null);

        foreach (var (user, role) in new[] { (teacher, "Teacher"), (students[0], "Student") })
        {
            var client = _factory.CreateClient();
            TestAuth.Set(client, user, role);
            using (var res = await client.GetAsync("/admin/log-uploads"))
                Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
            using (var res = await client.PostAsJsonAsync(Download, request))
                Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
            using (var res = await client.GetAsync(History))
                Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        }
        Assert.Empty(await RecordsAsync(_factory, session.Id));
    }

    // ------- Helpers -------

    private sealed record Seeded(
        User Admin, Session Session, Class Class, IReadOnlyList<User> Students, Dictionary<Guid, byte[]> Contents);

    /// <summary>An admin and a past session whose students each have an upload.</summary>
    private static async Task<Seeded> SeedAsync(Factory factory, int studentCount)
    {
        var (teacher, students, @class) = await TestSeed.SeedClassWithTeacherAndStudentsAsync(factory, studentCount);
        var admin = await TestSeed.AddUserAsync(factory, UserRole.Admin, "Admin " + Guid.NewGuid().ToString("N")[..6]);
        var session = await TestSeed.AddSessionAsync(factory, teacher.Id, @class.Id, students.Select(s => s.Id).ToList(), ended: true);
        var contents = new Dictionary<Guid, byte[]>();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        foreach (var student in students)
        {
            var content = Gzip($"2026-11-23 08:30:00 [INF] {student.DisplayName}'s agent");
            contents[student.Id] = content;
            db.SessionLogUploads.Add(new SessionLogUpload
            {
                SessionId = session.Id,
                UserId = student.Id,
                WindowFrom = session.StartedAt.AddMinutes(-30),
                WindowTo = session.EndedAt!.Value.AddMinutes(10),
                IsComplete = true,
                UploadedAt = factory.Clock.GetUtcNow(),
                ContentLength = content.Length,
                Content = content,
            });
        }
        await db.SaveChangesAsync();
        return new Seeded(admin, session, @class, students, contents);
    }

    private static HttpClient AdminClient(AnchorApiFactory factory, User admin)
    {
        var client = factory.CreateClient();
        TestAuth.SetAdmin(client, admin);
        return client;
    }

    private static async Task<List<LogAccess>> RecordsAsync(AnchorApiFactory factory, Guid sessionId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        return await db.LogAccesses.AsNoTracking().Where(a => a.SessionId == sessionId).ToListAsync();
    }

    private static string? FileName(HttpResponseMessage res) =>
        res.Content.Headers.ContentDisposition?.FileNameStar ?? res.Content.Headers.ContentDisposition?.FileName?.Trim('"');

    private static bool IsGzip(byte[] body) => body.Length >= 2 && body[0] == 0x1f && body[1] == 0x8b;

    private static byte[] Gzip(string text)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal))
            gzip.Write(Encoding.UTF8.GetBytes(text));
        return buffer.ToArray();
    }

    /// <summary>Fails every save that adds an access record, as a database that refuses the write would.</summary>
    private sealed class RefuseLogAccess : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<LogAccess>().Any(e => e.State == EntityState.Added))
                throw new DbUpdateException("The access record could not be stored.");
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
