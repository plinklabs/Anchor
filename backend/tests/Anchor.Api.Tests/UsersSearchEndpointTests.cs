using System.Net;
using System.Net.Http.Json;
using Anchor.Api.Controllers;
using Anchor.Api.Tests.FakeAuth;
using Anchor.Api.Users;
using Microsoft.Extensions.DependencyInjection;

namespace Anchor.Api.Tests;

public sealed class UsersSearchEndpointTests : IClassFixture<AnchorApiFactory>
{
    private readonly AnchorApiFactory _factory;

    public UsersSearchEndpointTests(AnchorApiFactory factory)
    {
        _factory = factory;
    }

    private FakeUserDirectorySearch Fake()
        => _factory.Services.GetRequiredService<FakeUserDirectorySearch>();

    [Fact]
    public async Task GET_users_search_unauthenticated_returns_401()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/users/search?q=ali");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GET_users_search_as_student_returns_403()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);

        using var client = _factory.CreateClient();
        TestAuth.SetStudent(client, scenario.Students[0]);

        var response = await client.GetAsync("/users/search?q=ali");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GET_users_search_returns_400_for_too_short_query()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var response = await client.GetAsync("/users/search?q=a");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GET_users_search_returns_400_for_too_long_query()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var q = new string('x', UsersController.MaxQueryLength + 1);
        var response = await client.GetAsync($"/users/search?q={q}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GET_users_search_trims_whitespace_and_passes_query_through()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var fake = Fake();
        var matched = new DirectoryUser(Guid.NewGuid(), "Alice Example", "alice@example.com", null, null);
        fake.Handler = (_, _, _, _) => Task.FromResult<IReadOnlyList<DirectoryUser>>(new[] { matched });

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var response = await client.GetAsync("/users/search?q=%20ali%20");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<List<DirectoryUserResponse>>();

        Assert.Equal("ali", fake.LastQuery);
        Assert.Equal(UsersController.DefaultTop, fake.LastTop);
        Assert.NotNull(body);
        var only = Assert.Single(body!);
        Assert.Equal(matched.EntraOid, only.EntraOid);
        Assert.Equal(matched.DisplayName, only.DisplayName);
        Assert.Equal(matched.Upn, only.Upn);
    }

    [Fact]
    public async Task GET_users_search_clamps_top_to_max()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var fake = Fake();
        fake.Handler = (_, _, _, _) => Task.FromResult<IReadOnlyList<DirectoryUser>>(Array.Empty<DirectoryUser>());

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var response = await client.GetAsync($"/users/search?q=ali&top={UsersController.MaxTop * 4}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(UsersController.MaxTop, fake.LastTop);
    }

    [Fact]
    public async Task GET_users_search_returns_502_when_directory_throws()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var fake = Fake();
        fake.Handler = (_, _, _, _) => throw new InvalidOperationException("consent required");

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var response = await client.GetAsync("/users/search?q=ali");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task GET_users_search_forwards_company_filter_and_returns_company_department()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var fake = Fake();
        var matched = new DirectoryUser(Guid.NewGuid(), "Alice Example", "alice@ssm.be", "SSM", "3A");
        fake.Handler = (_, _, _, _) => Task.FromResult<IReadOnlyList<DirectoryUser>>(new[] { matched });

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var response = await client.GetAsync("/users/search?q=ali&company=SSM");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<List<DirectoryUserResponse>>();

        Assert.Equal("SSM", fake.LastCompany);
        var only = Assert.Single(body!);
        Assert.Equal("SSM", only.Company);
        Assert.Equal("3A", only.Department);
    }

    [Fact]
    public async Task GET_users_search_blank_company_is_treated_as_no_filter()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var fake = Fake();
        fake.Handler = (_, _, _, _) => Task.FromResult<IReadOnlyList<DirectoryUser>>(Array.Empty<DirectoryUser>());

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var response = await client.GetAsync("/users/search?q=ali&company=%20%20");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(fake.LastCompany);
    }
}
