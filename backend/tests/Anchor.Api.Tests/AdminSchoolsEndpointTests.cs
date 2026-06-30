using System.Net;
using System.Net.Http.Json;
using Anchor.Api.Controllers;
using Anchor.Api.Tests.FakeAuth;
using Anchor.Domain.Users;
using Microsoft.Extensions.DependencyInjection;

namespace Anchor.Api.Tests;

/// <summary>
/// The "Schools" admin sub-tab surface (#301): the admin-gated list
/// (<c>GET /admin/schools</c>) that reconciles the live Entra companies with the
/// persisted per-school active state, and the activation toggle
/// (<c>POST /admin/schools/activation</c>). New schools default to active, so the
/// table only ever stores deactivations.
/// </summary>
public sealed class AdminSchoolsEndpointTests : IClassFixture<AnchorApiFactory>
{
    private readonly AnchorApiFactory _factory;

    public AdminSchoolsEndpointTests(AnchorApiFactory factory)
    {
        _factory = factory;
    }

    private FakeUserDirectorySearch Fake()
        => _factory.Services.GetRequiredService<FakeUserDirectorySearch>();

    // ---------- GET /admin/schools ----------

    [Fact]
    public async Task GET_schools_lists_graph_companies_active_by_default()
    {
        var token = Suffix();
        var ssm = $"SSM {token}";
        var sji = $"SJI {token}";
        var admin = await TestSeed.AddUserAsync(_factory, UserRole.Admin, "Caller " + token);
        Fake().ListCompaniesHandler = _ =>
            Task.FromResult<IReadOnlyList<string>>(new[] { ssm, sji });

        using var client = _factory.CreateClient();
        TestAuth.SetAdmin(client, admin);

        var schools = await client.GetFromJsonAsync<List<SchoolResponse>>("/admin/schools");

        Assert.NotNull(schools);
        Assert.Contains(schools!, s => s.Name == ssm && s.IsActive);
        Assert.Contains(schools!, s => s.Name == sji && s.IsActive);
    }

    [Fact]
    public async Task GET_schools_reflects_persisted_deactivation()
    {
        var token = Suffix();
        var hidden = $"Hidden {token}";
        var visible = $"Visible {token}";
        var admin = await TestSeed.AddUserAsync(_factory, UserRole.Admin, "Caller " + token);
        await TestSeed.AddSchoolAsync(_factory, hidden, isActive: false);
        Fake().ListCompaniesHandler = _ =>
            Task.FromResult<IReadOnlyList<string>>(new[] { hidden, visible });

        using var client = _factory.CreateClient();
        TestAuth.SetAdmin(client, admin);

        var schools = await client.GetFromJsonAsync<List<SchoolResponse>>("/admin/schools");

        Assert.NotNull(schools);
        Assert.Contains(schools!, s => s.Name == hidden && !s.IsActive);
        Assert.Contains(schools!, s => s.Name == visible && s.IsActive);
    }

    [Fact]
    public async Task GET_schools_includes_persisted_school_absent_from_graph()
    {
        var token = Suffix();
        var orphan = $"Orphan {token}";
        var admin = await TestSeed.AddUserAsync(_factory, UserRole.Admin, "Caller " + token);
        // Persisted (an admin once curated it) but no longer in the Entra scan.
        await TestSeed.AddSchoolAsync(_factory, orphan, isActive: false);
        Fake().ListCompaniesHandler = _ =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        using var client = _factory.CreateClient();
        TestAuth.SetAdmin(client, admin);

        var schools = await client.GetFromJsonAsync<List<SchoolResponse>>("/admin/schools");

        Assert.NotNull(schools);
        Assert.Contains(schools!, s => s.Name == orphan && !s.IsActive);
    }

    [Fact]
    public async Task GET_schools_degrades_to_persisted_only_when_graph_throws()
    {
        var token = Suffix();
        var known = $"Known {token}";
        var admin = await TestSeed.AddUserAsync(_factory, UserRole.Admin, "Caller " + token);
        await TestSeed.AddSchoolAsync(_factory, known, isActive: false);
        // #281: Graph consent/outage must not 502 the admin list.
        Fake().ListCompaniesHandler = _ => throw new InvalidOperationException("consent required");

        using var client = _factory.CreateClient();
        TestAuth.SetAdmin(client, admin);

        var response = await client.GetAsync("/admin/schools");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var schools = await response.Content.ReadFromJsonAsync<List<SchoolResponse>>();
        Assert.NotNull(schools);
        Assert.Contains(schools!, s => s.Name == known && !s.IsActive);
    }

    [Fact]
    public async Task GET_schools_as_teacher_returns_403()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var response = await client.GetAsync("/admin/schools");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GET_schools_as_student_returns_403()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        using var client = _factory.CreateClient();
        TestAuth.SetStudent(client, scenario.Students[0]);

        var response = await client.GetAsync("/admin/schools");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------- POST /admin/schools/activation ----------

    [Fact]
    public async Task POST_activation_persists_deactivation_then_reactivation()
    {
        var token = Suffix();
        var school = $"Toggle {token}";
        var admin = await TestSeed.AddUserAsync(_factory, UserRole.Admin, "Caller " + token);
        Fake().ListCompaniesHandler = _ =>
            Task.FromResult<IReadOnlyList<string>>(new[] { school });

        using var client = _factory.CreateClient();
        TestAuth.SetAdmin(client, admin);

        // Deactivate (creates the row).
        var off = await client.PostAsJsonAsync(
            "/admin/schools/activation", new { name = school, isActive = false });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        var offState = await off.Content.ReadFromJsonAsync<SchoolResponse>();
        Assert.NotNull(offState);
        Assert.False(offState!.IsActive);

        // The deactivation is visible in the admin list.
        var listAfterOff = await client.GetFromJsonAsync<List<SchoolResponse>>("/admin/schools");
        Assert.Contains(listAfterOff!, s => s.Name == school && !s.IsActive);

        // Reactivate (updates the existing row, doesn't insert a duplicate).
        var on = await client.PostAsJsonAsync(
            "/admin/schools/activation", new { name = school, isActive = true });
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        var onState = await on.Content.ReadFromJsonAsync<SchoolResponse>();
        Assert.True(onState!.IsActive);

        var listAfterOn = await client.GetFromJsonAsync<List<SchoolResponse>>("/admin/schools");
        Assert.Single(listAfterOn!, s => s.Name == school);
        Assert.Contains(listAfterOn!, s => s.Name == school && s.IsActive);
    }

    [Fact]
    public async Task POST_activation_without_name_returns_400()
    {
        var admin = await TestSeed.AddUserAsync(_factory, UserRole.Admin, "Caller " + Suffix());
        using var client = _factory.CreateClient();
        TestAuth.SetAdmin(client, admin);

        var response = await client.PostAsJsonAsync(
            "/admin/schools/activation", new { name = "   ", isActive = false });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task POST_activation_as_teacher_returns_403()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var response = await client.PostAsJsonAsync(
            "/admin/schools/activation", new { name = "Anything", isActive = false });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static string Suffix() => Guid.NewGuid().ToString("N").Substring(0, 6);
}
