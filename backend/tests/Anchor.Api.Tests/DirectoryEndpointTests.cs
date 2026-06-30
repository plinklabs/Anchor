using System.Net;
using System.Net.Http.Json;
using Anchor.Api.Tests.FakeAuth;
using Microsoft.Extensions.DependencyInjection;

namespace Anchor.Api.Tests;

public sealed class DirectoryEndpointTests : IClassFixture<AnchorApiFactory>
{
    private readonly AnchorApiFactory _factory;

    public DirectoryEndpointTests(AnchorApiFactory factory)
    {
        _factory = factory;
    }

    private FakeUserDirectorySearch Fake()
        => _factory.Services.GetRequiredService<FakeUserDirectorySearch>();

    [Fact]
    public async Task GET_directory_schools_unauthenticated_returns_401()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/directory/schools");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GET_directory_schools_as_student_returns_403()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);

        using var client = _factory.CreateClient();
        TestAuth.SetStudent(client, scenario.Students[0]);

        var response = await client.GetAsync("/directory/schools");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GET_directory_schools_falls_back_to_live_companies_when_unconfigured()
    {
        // No school has been reconciled yet: the selector should still show every
        // live company (today's behaviour) until an admin curates (#301).
        await TestSeed.ClearSchoolsAsync(_factory);
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var fake = Fake();
        fake.ListCompaniesHandler = _ => Task.FromResult<IReadOnlyList<string>>(new[] { "SSM", "SJI" });

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var schools = await client.GetFromJsonAsync<List<string>>("/directory/schools");

        Assert.NotNull(schools);
        Assert.Equal(new[] { "SSM", "SJI" }, schools!);
    }

    [Fact]
    public async Task GET_directory_schools_serves_active_schools_from_db_without_graph()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var token = Guid.NewGuid().ToString("N").Substring(0, 6);
        var active = $"Active {token}";
        var hidden = $"Hidden {token}";
        // Configured: the table is the source of truth, so the deactivated school
        // is filtered out and Graph is never consulted on this hot path.
        await TestSeed.AddSchoolAsync(_factory, active, isActive: true);
        await TestSeed.AddSchoolAsync(_factory, hidden, isActive: false);
        var fake = Fake();
        var before = fake.ListCompaniesCallCount;

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var schools = await client.GetFromJsonAsync<List<string>>("/directory/schools");

        Assert.NotNull(schools);
        Assert.Contains(active, schools!);
        Assert.DoesNotContain(hidden, schools!);
        // The DB-backed read must not page the directory.
        Assert.Equal(before, fake.ListCompaniesCallCount);
    }

    [Fact]
    public async Task GET_directory_schools_returns_502_when_unconfigured_and_directory_throws()
    {
        // Only the unconfigured fallback touches Graph, so the 502 surfaces while
        // the table is still empty; a configured (DB-backed) read never throws.
        await TestSeed.ClearSchoolsAsync(_factory);
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var fake = Fake();
        fake.ListCompaniesHandler = _ => throw new InvalidOperationException("consent required");

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var response = await client.GetAsync("/directory/schools");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }
}
