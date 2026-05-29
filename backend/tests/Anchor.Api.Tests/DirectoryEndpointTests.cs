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
    public async Task GET_directory_schools_returns_company_list()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var fake = Fake();
        fake.ListCompaniesHandler = _ => Task.FromResult<IReadOnlyList<string>>(new[] { "SSM", "SJI" });

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var schools = await client.GetFromJsonAsync<List<string>>("/directory/schools");

        Assert.NotNull(schools);
        Assert.Equal(new[] { "SSM", "SJI" }, schools!);
        Assert.Equal(1, fake.ListCompaniesCallCount);
    }

    [Fact]
    public async Task GET_directory_schools_returns_502_when_directory_throws()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var fake = Fake();
        fake.ListCompaniesHandler = _ => throw new InvalidOperationException("consent required");

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var response = await client.GetAsync("/directory/schools");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }
}
