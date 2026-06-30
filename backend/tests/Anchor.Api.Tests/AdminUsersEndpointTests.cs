using System.Net;
using System.Net.Http.Json;
using Anchor.Api.Controllers;
using Anchor.Domain.Users;
using Anchor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Anchor.Api.Tests;

/// <summary>
/// The "Manage admins" surface (#300): the admin-gated read endpoints
/// (<c>GET /admin/users/admins</c>, <c>GET /admin/users/candidates</c>) and the
/// <c>POST /me/demote</c> mutation, including the last-admin lockout guard.
/// </summary>
public sealed class AdminUsersEndpointTests : IClassFixture<AnchorApiFactory>
{
    private readonly AnchorApiFactory _factory;

    public AdminUsersEndpointTests(AnchorApiFactory factory)
    {
        _factory = factory;
    }

    // ---------- GET /admin/users/admins ----------

    [Fact]
    public async Task GET_admins_lists_admins_only()
    {
        var admin = await TestSeed.AddUserAsync(_factory, UserRole.Admin, "ListAdmin " + Suffix());
        var teacher = await TestSeed.AddUserAsync(_factory, UserRole.Teacher, "ListTeacher " + Suffix());

        using var client = _factory.CreateClient();
        TestAuth.SetAdmin(client, admin);

        var list = await client.GetFromJsonAsync<List<AdminUserResponse>>("/admin/users/admins");

        Assert.NotNull(list);
        Assert.Contains(list!, u => u.Id == admin.Id && u.Role == UserRole.Admin);
        Assert.DoesNotContain(list!, u => u.Id == teacher.Id);
    }

    [Fact]
    public async Task GET_admins_as_teacher_returns_403()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var res = await client.GetAsync("/admin/users/admins");

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task GET_admins_as_student_returns_403()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        using var client = _factory.CreateClient();
        TestAuth.SetStudent(client, scenario.Students[0]);

        var res = await client.GetAsync("/admin/users/admins");

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    // ---------- GET /admin/users/candidates ----------

    [Fact]
    public async Task GET_candidates_excludes_admins_and_matches_query_case_insensitively()
    {
        var token = Suffix();
        var admin = await TestSeed.AddUserAsync(_factory, UserRole.Admin, $"Caller {token}");
        var teacher = await TestSeed.AddUserAsync(_factory, UserRole.Teacher, $"Findme Teacher {token}");
        var student = await TestSeed.AddUserAsync(_factory, UserRole.Student, $"Findme Student {token}");
        var otherAdmin = await TestSeed.AddUserAsync(_factory, UserRole.Admin, $"Findme Admin {token}");

        using var client = _factory.CreateClient();
        TestAuth.SetAdmin(client, admin);

        // Lowercased query against the capitalised "Findme" names proves the
        // match ignores case (column-collation-agnostic). The DB is isolated per
        // test class, so only this test's seeded users carry the token.
        var results = await client.GetFromJsonAsync<List<AdminUserResponse>>(
            "/admin/users/candidates?query=findme");

        Assert.NotNull(results);
        Assert.Contains(results!, u => u.Id == teacher.Id);
        Assert.Contains(results!, u => u.Id == student.Id);
        // Existing admins are not promotion candidates.
        Assert.DoesNotContain(results!, u => u.Id == otherAdmin.Id);
        Assert.DoesNotContain(results!, u => u.Role == UserRole.Admin);
    }

    [Fact]
    public async Task GET_candidates_with_no_match_returns_empty()
    {
        var admin = await TestSeed.AddUserAsync(_factory, UserRole.Admin, "Caller " + Suffix());
        using var client = _factory.CreateClient();
        TestAuth.SetAdmin(client, admin);

        var results = await client.GetFromJsonAsync<List<AdminUserResponse>>(
            $"/admin/users/candidates?query=no-such-user-{Suffix()}");

        Assert.NotNull(results);
        Assert.Empty(results!);
    }

    [Fact]
    public async Task GET_candidates_as_teacher_returns_403()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var res = await client.GetAsync("/admin/users/candidates?query=anything");

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    // ---------- POST /me/demote ----------

    [Fact]
    public async Task POST_demote_as_admin_sets_target_back_to_teacher()
    {
        var caller = await TestSeed.AddUserAsync(_factory, UserRole.Admin, "Caller " + Suffix());
        var target = await TestSeed.AddUserAsync(_factory, UserRole.Admin, "Target " + Suffix());

        using var client = _factory.CreateClient();
        TestAuth.SetAdmin(client, caller);

        var res = await client.PostAsJsonAsync("/me/demote", new { userId = target.Id });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var detail = await res.Content.ReadFromJsonAsync<MeResponse>();
        Assert.NotNull(detail);
        Assert.Equal(UserRole.Teacher, detail!.Role);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        var refreshed = await db.Users.AsNoTracking().SingleAsync(u => u.Id == target.Id);
        Assert.Equal(UserRole.Teacher, refreshed.Role);
    }

    [Fact]
    public async Task POST_demote_refuses_to_remove_the_last_admin()
    {
        // Make the seeded admin the only one in the DB so the guard fires
        // deterministically regardless of what other tests left behind.
        await DemoteAllAdminsAsync();
        var solo = await TestSeed.AddUserAsync(_factory, UserRole.Admin, "Solo " + Suffix());

        using var client = _factory.CreateClient();
        TestAuth.SetAdmin(client, solo);

        var res = await client.PostAsJsonAsync("/me/demote", new { userId = solo.Id });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        var refreshed = await db.Users.AsNoTracking().SingleAsync(u => u.Id == solo.Id);
        Assert.Equal(UserRole.Admin, refreshed.Role);
    }

    [Fact]
    public async Task POST_demote_non_admin_target_is_a_no_op()
    {
        var caller = await TestSeed.AddUserAsync(_factory, UserRole.Admin, "Caller " + Suffix());
        var target = await TestSeed.AddUserAsync(_factory, UserRole.Teacher, "AlreadyTeacher " + Suffix());

        using var client = _factory.CreateClient();
        TestAuth.SetAdmin(client, caller);

        var res = await client.PostAsJsonAsync("/me/demote", new { userId = target.Id });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var detail = await res.Content.ReadFromJsonAsync<MeResponse>();
        Assert.NotNull(detail);
        Assert.Equal(UserRole.Teacher, detail!.Role);
    }

    [Fact]
    public async Task POST_demote_unknown_user_returns_404()
    {
        var caller = await TestSeed.AddUserAsync(_factory, UserRole.Admin, "Caller " + Suffix());
        using var client = _factory.CreateClient();
        TestAuth.SetAdmin(client, caller);

        var res = await client.PostAsJsonAsync("/me/demote", new { userId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task POST_demote_as_teacher_returns_403()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var target = await TestSeed.AddUserAsync(_factory, UserRole.Admin, "Target " + Suffix());

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var res = await client.PostAsJsonAsync("/me/demote", new { userId = target.Id });

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    private async Task DemoteAllAdminsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        var admins = await db.Users.Where(u => u.Role == UserRole.Admin).ToListAsync();
        foreach (var a in admins)
            a.Role = UserRole.Teacher;
        await db.SaveChangesAsync();
    }

    private static string Suffix() => Guid.NewGuid().ToString("N").Substring(0, 6);
}
