using System.Net;
using System.Net.Http.Json;
using Anchor.Api.Controllers;
using Anchor.Api.Sessions;
using Anchor.Domain.Bundles;
using Anchor.Domain.Sessions;
using Anchor.Domain.Users;
using Anchor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Anchor.Api.Tests;

public sealed class BundlesAdminEndpointTests : IClassFixture<AnchorApiFactory>
{
    private readonly AnchorApiFactory _factory;

    public BundlesAdminEndpointTests(AnchorApiFactory factory)
    {
        _factory = factory;
    }

    // ---------- POST ----------

    [Fact]
    public async Task POST_bundle_as_teacher_returns_403()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var res = await client.PostAsJsonAsync("/bundles", NewBundleRequest("Geo-" + Suffix()));

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task POST_bundle_as_student_returns_403()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        using var client = _factory.CreateClient();
        TestAuth.SetStudent(client, scenario.Students[0]);

        var res = await client.PostAsJsonAsync("/bundles", NewBundleRequest("Geo-" + Suffix()));

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task POST_bundle_as_admin_creates_version_1()
    {
        var admin = await TestSeed.AddUserAsync(_factory, UserRole.Admin, "Admin " + Suffix());
        using var client = _factory.CreateClient();
        TestAuth.SetAdmin(client, admin);

        var name = "GeoGebra-" + Suffix();
        var res = await client.PostAsJsonAsync("/bundles", NewBundleRequest(name));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);

        var detail = await res.Content.ReadFromJsonAsync<BundleDetail>();
        Assert.NotNull(detail);
        Assert.Equal(name, detail!.Name);
        Assert.Equal(1, detail.Version);
        Assert.False(detail.IsArchived);
        Assert.Single(detail.Entries);
        Assert.Equal("*.geogebra.org", detail.Entries[0].Value);
    }

    [Fact]
    public async Task POST_bundle_rejects_duplicate_name()
    {
        var admin = await TestSeed.AddUserAsync(_factory, UserRole.Admin, "Admin " + Suffix());
        var existing = await TestSeed.AddBundleAsync(_factory, "Dup-" + Suffix());

        using var client = _factory.CreateClient();
        TestAuth.SetAdmin(client, admin);

        var res = await client.PostAsJsonAsync("/bundles", NewBundleRequest(existing.Name));

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task POST_bundle_rejects_invalid_domain()
    {
        var admin = await TestSeed.AddUserAsync(_factory, UserRole.Admin, "Admin " + Suffix());

        using var client = _factory.CreateClient();
        TestAuth.SetAdmin(client, admin);

        var body = new WriteBundleRequest("Bad-" + Suffix(), new[]
        {
            new WriteBundleEntry(BundleEntryKind.Domain, "not a domain!", BundleEntryMatchType.Exact),
        });
        var res = await client.PostAsJsonAsync("/bundles", body);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task POST_bundle_rejects_processname_with_exe_suffix()
    {
        var admin = await TestSeed.AddUserAsync(_factory, UserRole.Admin, "Admin " + Suffix());

        using var client = _factory.CreateClient();
        TestAuth.SetAdmin(client, admin);

        var body = new WriteBundleRequest("App-" + Suffix(), new[]
        {
            new WriteBundleEntry(BundleEntryKind.App, "msedge.exe", BundleEntryMatchType.Exact),
        });
        var res = await client.PostAsJsonAsync("/bundles", body);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // ---------- PUT ----------

    [Fact]
    public async Task PUT_bundle_bumps_version_and_replaces_entries()
    {
        var admin = await TestSeed.AddUserAsync(_factory, UserRole.Admin, "Admin " + Suffix());
        var bundle = await TestSeed.AddBundleAsync(_factory, "Bingel-" + Suffix());
        await TestSeed.AddBundleEntryAsync(_factory, bundle.Id, BundleEntryKind.Domain,
            "old.example.com", BundleEntryMatchType.Exact);

        using var client = _factory.CreateClient();
        TestAuth.SetAdmin(client, admin);

        var updated = new WriteBundleRequest(bundle.Name, new[]
        {
            new WriteBundleEntry(BundleEntryKind.Domain, "*.new.example.com", BundleEntryMatchType.Wildcard),
            new WriteBundleEntry(BundleEntryKind.Domain, "other.example.org", BundleEntryMatchType.Exact),
        });
        var res = await client.PutAsJsonAsync($"/bundles/{bundle.Id}", updated);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var detail = await res.Content.ReadFromJsonAsync<BundleDetail>();
        Assert.NotNull(detail);
        Assert.Equal(2, detail!.Version);
        Assert.Equal(2, detail.Entries.Count);
        Assert.DoesNotContain(detail.Entries, e => e.Value == "old.example.com");
        Assert.Contains(detail.Entries, e => e.Value == "*.new.example.com");
    }

    [Fact]
    public async Task PUT_bundle_unarchives()
    {
        var admin = await TestSeed.AddUserAsync(_factory, UserRole.Admin, "Admin " + Suffix());
        var bundle = await TestSeed.AddBundleAsync(_factory, "ArchivedRestore-" + Suffix(), isArchived: true);

        using var client = _factory.CreateClient();
        TestAuth.SetAdmin(client, admin);

        var body = NewBundleRequest(bundle.Name);
        var res = await client.PutAsJsonAsync($"/bundles/{bundle.Id}", body);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var detail = await res.Content.ReadFromJsonAsync<BundleDetail>();
        Assert.NotNull(detail);
        Assert.False(detail!.IsArchived);
    }

    [Fact]
    public async Task PUT_bundle_as_teacher_returns_403()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var bundle = await TestSeed.AddBundleAsync(_factory, "T-" + Suffix());

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var res = await client.PutAsJsonAsync($"/bundles/{bundle.Id}", NewBundleRequest(bundle.Name));

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    // ---------- DELETE (soft archive) ----------

    [Fact]
    public async Task DELETE_bundle_archives_and_hides_from_default_list_but_keeps_session_resolution()
    {
        var admin = await TestSeed.AddUserAsync(_factory, UserRole.Admin, "Admin " + Suffix());
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var bundle = await TestSeed.AddBundleAsync(_factory, "Soft-" + Suffix());
        await TestSeed.AddBundleEntryAsync(_factory, bundle.Id, BundleEntryKind.Domain,
            "history.example.com", BundleEntryMatchType.Exact);

        // Past session referencing this bundle so we can prove historical resolution survives.
        var session = await TestSeed.AddSessionAsync(_factory, scenario.Teacher.Id, scenario.Class.Id,
            new[] { scenario.Students[0].Id }, ended: true);
        await AddSessionBundleAsync(session.Id, bundle.Id);

        using var client = _factory.CreateClient();
        TestAuth.SetAdmin(client, admin);

        var delRes = await client.DeleteAsync($"/bundles/{bundle.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delRes.StatusCode);

        // Default list excludes archived.
        var list = await client.GetFromJsonAsync<List<BundleSummary>>("/bundles");
        Assert.NotNull(list);
        Assert.DoesNotContain(list!, b => b.Id == bundle.Id);

        // Opt-in via ?includeArchived=true surfaces it for the catalogue editor.
        var listAll = await client.GetFromJsonAsync<List<BundleSummary>>("/bundles?includeArchived=true");
        Assert.NotNull(listAll);
        Assert.Contains(listAll!, b => b.Id == bundle.Id && b.IsArchived);

        // Historical resolution: the allowlist expander still finds the entries
        // when expanding for the past session, so audit views can render the
        // allowlist that was in force.
        using var scope = _factory.Services.CreateScope();
        var expander = scope.ServiceProvider.GetRequiredService<ISessionAllowlistExpander>();
        var expanded = await expander.ExpandForSessionAsync(session.Id);
        Assert.Contains(expanded.Domains, d => d.Value == "history.example.com");
    }

    [Fact]
    public async Task DELETE_bundle_as_teacher_returns_403()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var bundle = await TestSeed.AddBundleAsync(_factory, "T-" + Suffix());

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var res = await client.DeleteAsync($"/bundles/{bundle.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    // ---------- DELETE (hard) ----------

    [Fact]
    public async Task DELETE_bundle_hard_succeeds_when_never_used_and_removes_row()
    {
        var admin = await TestSeed.AddUserAsync(_factory, UserRole.Admin, "Admin " + Suffix());
        var bundle = await TestSeed.AddBundleAsync(_factory, "Hard-" + Suffix());
        await TestSeed.AddBundleEntryAsync(_factory, bundle.Id, BundleEntryKind.Domain,
            "neverused.example.com", BundleEntryMatchType.Exact);

        using var client = _factory.CreateClient();
        TestAuth.SetAdmin(client, admin);

        var res = await client.DeleteAsync($"/bundles/{bundle.Id}?hard=true");
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);

        // Row is gone — both default list and includeArchived list omit it.
        var listAll = await client.GetFromJsonAsync<List<BundleSummary>>("/bundles?includeArchived=true");
        Assert.NotNull(listAll);
        Assert.DoesNotContain(listAll!, b => b.Id == bundle.Id);

        var get = await client.GetAsync($"/bundles/{bundle.Id}");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task DELETE_bundle_hard_returns_409_when_used_in_session()
    {
        var admin = await TestSeed.AddUserAsync(_factory, UserRole.Admin, "Admin " + Suffix());
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var bundle = await TestSeed.AddBundleAsync(_factory, "Used-" + Suffix());
        var session = await TestSeed.AddSessionAsync(_factory, scenario.Teacher.Id, scenario.Class.Id,
            new[] { scenario.Students[0].Id }, ended: true);
        await AddSessionBundleAsync(session.Id, bundle.Id);

        using var client = _factory.CreateClient();
        TestAuth.SetAdmin(client, admin);

        var res = await client.DeleteAsync($"/bundles/{bundle.Id}?hard=true");
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);

        // Row survives, and archive flag is untouched (no implicit soft delete).
        var detail = await client.GetFromJsonAsync<BundleDetail>($"/bundles/{bundle.Id}");
        Assert.NotNull(detail);
        Assert.False(detail!.IsArchived);
        Assert.True(detail.HasBeenUsed);
    }

    [Fact]
    public async Task DELETE_bundle_hard_as_teacher_returns_403()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var bundle = await TestSeed.AddBundleAsync(_factory, "T-" + Suffix());

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var res = await client.DeleteAsync($"/bundles/{bundle.Id}?hard=true");

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task GET_bundles_exposes_hasBeenUsed_flag()
    {
        var admin = await TestSeed.AddUserAsync(_factory, UserRole.Admin, "Admin " + Suffix());
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var unused = await TestSeed.AddBundleAsync(_factory, "Unused-" + Suffix());
        var used = await TestSeed.AddBundleAsync(_factory, "Used-" + Suffix());
        var session = await TestSeed.AddSessionAsync(_factory, scenario.Teacher.Id, scenario.Class.Id,
            new[] { scenario.Students[0].Id }, ended: true);
        await AddSessionBundleAsync(session.Id, used.Id);

        using var client = _factory.CreateClient();
        TestAuth.SetAdmin(client, admin);

        var list = await client.GetFromJsonAsync<List<BundleSummary>>("/bundles");
        Assert.NotNull(list);
        Assert.False(list!.Single(b => b.Id == unused.Id).HasBeenUsed);
        Assert.True(list.Single(b => b.Id == used.Id).HasBeenUsed);

        var unusedDetail = await client.GetFromJsonAsync<BundleDetail>($"/bundles/{unused.Id}");
        Assert.NotNull(unusedDetail);
        Assert.False(unusedDetail!.HasBeenUsed);

        var usedDetail = await client.GetFromJsonAsync<BundleDetail>($"/bundles/{used.Id}");
        Assert.NotNull(usedDetail);
        Assert.True(usedDetail!.HasBeenUsed);
    }

    // ---------- GET list filtering ----------

    [Fact]
    public async Task GET_bundles_hides_archived_by_default()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var active = await TestSeed.AddBundleAsync(_factory, "Active-" + Suffix());
        var archived = await TestSeed.AddBundleAsync(_factory, "Archived-" + Suffix(), isArchived: true);

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var body = await client.GetFromJsonAsync<List<BundleSummary>>("/bundles");
        Assert.NotNull(body);
        Assert.Contains(body!, b => b.Id == active.Id);
        Assert.DoesNotContain(body!, b => b.Id == archived.Id);
    }

    // ---------- Admin DB role recognised by policy even without claim ----------

    [Fact]
    public async Task POST_bundle_succeeds_when_user_has_admin_in_DB_only()
    {
        // The user's JWT carries Teacher role (no Admin claim); the DB row
        // has Admin. AdminRoleAuthorizationHandler should consult the DB and
        // succeed even though the principal does not advertise Admin. Mirrors
        // the real /me?promote-me=admin flow where the dashboard's bearer
        // token still says "Teacher" but the DB role is "Admin".
        var dbAdmin = await TestSeed.AddUserAsync(_factory, UserRole.Admin, "Hidden Admin " + Suffix());

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, dbAdmin);

        var res = await client.PostAsJsonAsync("/bundles", NewBundleRequest("DbAdmin-" + Suffix()));

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
    }

    // ---------- /me/promote ----------

    [Fact]
    public async Task POST_me_promote_as_admin_promotes_target()
    {
        var admin = await TestSeed.AddUserAsync(_factory, UserRole.Admin, "Admin " + Suffix());
        var target = await TestSeed.AddUserAsync(_factory, UserRole.Teacher, "Target " + Suffix());

        using var client = _factory.CreateClient();
        TestAuth.SetAdmin(client, admin);

        var res = await client.PostAsJsonAsync("/me/promote", new { userId = target.Id });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var detail = await res.Content.ReadFromJsonAsync<MeResponse>();
        Assert.NotNull(detail);
        Assert.Equal(UserRole.Admin, detail!.Role);

        // Verify persisted.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        var refreshed = await db.Users.AsNoTracking().SingleAsync(u => u.Id == target.Id);
        Assert.Equal(UserRole.Admin, refreshed.Role);
    }

    [Fact]
    public async Task POST_me_promote_as_teacher_returns_403()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var target = await TestSeed.AddUserAsync(_factory, UserRole.Teacher, "Target " + Suffix());

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var res = await client.PostAsJsonAsync("/me/promote", new { userId = target.Id });

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    // ---------- /me upsert preserves Admin ----------

    [Fact]
    public async Task GET_me_does_not_downgrade_existing_admin()
    {
        // Provision an admin directly in the DB.
        var admin = await TestSeed.AddUserAsync(_factory, UserRole.Admin, "Persistent Admin");

        using var client = _factory.CreateClient();
        // Sign in as that same OID but with a Teacher JWT claim — the dashboard
        // does this on every refresh.
        TestAuth.SetTeacher(client, admin);

        var res = await client.GetAsync("/me");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var me = await res.Content.ReadFromJsonAsync<MeResponse>();
        Assert.NotNull(me);
        Assert.Equal(UserRole.Admin, me!.Role);
    }

    // ---------- helpers ----------

    private async Task AddSessionBundleAsync(Guid sessionId, Guid bundleId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        db.SessionBundles.Add(new SessionBundle { SessionId = sessionId, BundleId = bundleId });
        await db.SaveChangesAsync();
    }

    private static WriteBundleRequest NewBundleRequest(string name) => new(name, new[]
    {
        new WriteBundleEntry(BundleEntryKind.Domain, "*.geogebra.org", BundleEntryMatchType.Wildcard),
    });

    private static string Suffix() => Guid.NewGuid().ToString("N").Substring(0, 6);
}
