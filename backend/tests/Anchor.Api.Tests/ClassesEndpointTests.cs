using System.Net;
using System.Net.Http.Json;
using Anchor.Api.Controllers;

namespace Anchor.Api.Tests;

public sealed class ClassesEndpointTests : IClassFixture<AnchorApiFactory>
{
    private readonly AnchorApiFactory _factory;

    public ClassesEndpointTests(AnchorApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GET_classes_unauthenticated_returns_401()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/classes");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GET_classes_as_student_returns_403()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);

        using var client = _factory.CreateClient();
        TestAuth.SetStudent(client, scenario.Students[0]);

        var response = await client.GetAsync("/classes");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GET_classes_as_teacher_returns_only_classes_they_teach()
    {
        var taught = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        // Another class where this teacher is not a teacher.
        await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, taught.Teacher);

        var classes = await client.GetFromJsonAsync<List<ClassSummary>>("/classes");

        Assert.NotNull(classes);
        Assert.Single(classes!);
        Assert.Equal(taught.Class.Id, classes![0].Id);
        Assert.Equal(taught.Class.Name, classes[0].Name);
    }

    [Fact]
    public async Task GET_class_members_returns_404_for_missing_class()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var response = await client.GetAsync($"/classes/{Guid.NewGuid()}/members");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GET_class_members_returns_403_when_caller_is_not_a_teacher_of_that_class()
    {
        var owned = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var other = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, owned.Teacher);

        var response = await client.GetAsync($"/classes/{other.Class.Id}/members");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GET_class_members_returns_roster_for_owning_teacher()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory, studentCount: 3);

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var body = await client.GetFromJsonAsync<ClassMembersResponse>($"/classes/{scenario.Class.Id}/members");

        Assert.NotNull(body);
        Assert.Equal(scenario.Class.Id, body!.Id);
        Assert.Equal(4, body.Members.Count); // 1 teacher + 3 students
        Assert.Contains(body.Members, m => m.UserId == scenario.Teacher.Id);
        foreach (var student in scenario.Students)
            Assert.Contains(body.Members, m => m.UserId == student.Id);
    }
}
