using System.Net;
using System.Net.Http.Json;
using Anchor.Api.Controllers;
using Anchor.Domain.Classes;
using Anchor.Domain.Users;
using Anchor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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
        var teacherMember = Assert.Single(body.Members, m => m.UserId == scenario.Teacher.Id);
        Assert.Equal(scenario.Teacher.EntraOid, teacherMember.EntraOid);
        Assert.Equal(ClassMembershipRole.Teacher, teacherMember.MembershipRole);
        Assert.NotEqual(default, teacherMember.JoinedAt);
        foreach (var student in scenario.Students)
            Assert.Contains(body.Members, m => m.UserId == student.Id);
    }

    [Fact]
    public async Task POST_class_members_as_non_teacher_of_class_returns_403()
    {
        var owned = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var other = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, owned.Teacher);

        var response = await client.PostAsJsonAsync(
            $"/classes/{other.Class.Id}/members",
            new AddClassMemberRequest(Guid.NewGuid(), "Placeholder Name", ClassMembershipRole.Member));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task POST_class_members_adds_existing_user_and_is_idempotent()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var outsider = await TestSeed.AddUserAsync(_factory, UserRole.Student, "Outsider Existing");

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var first = await client.PostAsJsonAsync(
            $"/classes/{scenario.Class.Id}/members",
            new AddClassMemberRequest(outsider.EntraOid, null, ClassMembershipRole.Member));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<ClassMembershipImportResult>();
        Assert.NotNull(firstBody);
        Assert.Equal(ClassMembershipImportStatus.Added, firstBody!.Status);
        Assert.Equal(outsider.Id, firstBody.UserId);

        var again = await client.PostAsJsonAsync(
            $"/classes/{scenario.Class.Id}/members",
            new AddClassMemberRequest(outsider.EntraOid, null, ClassMembershipRole.Member));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var againBody = await again.Content.ReadFromJsonAsync<ClassMembershipImportResult>();
        Assert.Equal(ClassMembershipImportStatus.AlreadyMember, againBody!.Status);

        // Roster should contain the new member exactly once.
        var roster = await client.GetFromJsonAsync<ClassMembersResponse>($"/classes/{scenario.Class.Id}/members");
        Assert.Single(roster!.Members, m => m.UserId == outsider.Id);
    }

    [Fact]
    public async Task POST_class_members_creates_placeholder_user_when_unknown_oid_with_display_name()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var newOid = Guid.NewGuid();

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var response = await client.PostAsJsonAsync(
            $"/classes/{scenario.Class.Id}/members",
            new AddClassMemberRequest(newOid, "Placeholder Pat", ClassMembershipRole.Member));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ClassMembershipImportResult>();
        Assert.NotNull(body);
        Assert.Equal(ClassMembershipImportStatus.Added, body!.Status);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        var created = await db.Users.SingleAsync(u => u.EntraOid == newOid);
        Assert.Equal("Placeholder Pat", created.DisplayName);
        Assert.Equal(UserRole.Student, created.Role);
    }

    [Fact]
    public async Task POST_class_members_returns_400_when_unknown_oid_and_no_display_name()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var response = await client.PostAsJsonAsync(
            $"/classes/{scenario.Class.Id}/members",
            new AddClassMemberRequest(Guid.NewGuid(), null, ClassMembershipRole.Member));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DELETE_class_member_removes_membership_and_is_404_when_already_gone()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory, studentCount: 2);
        var target = scenario.Students[0];

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var first = await client.DeleteAsync($"/classes/{scenario.Class.Id}/members/{target.Id}");
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        // Confirm removal landed in DB.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
            var stillThere = await db.ClassMemberships.AnyAsync(
                m => m.ClassId == scenario.Class.Id && m.UserId == target.Id);
            Assert.False(stillThere);
        }

        var again = await client.DeleteAsync($"/classes/{scenario.Class.Id}/members/{target.Id}");
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task DELETE_class_member_as_non_teacher_of_class_returns_403()
    {
        var owned = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var other = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, owned.Teacher);

        var response = await client.DeleteAsync(
            $"/classes/{other.Class.Id}/members/{other.Students[0].Id}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task POST_class_members_import_processes_rows_with_per_row_results()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory, studentCount: 1);
        var existingStudent = scenario.Students[0];
        var newOid = Guid.NewGuid();

        var rows = new List<ImportClassMemberRow>
        {
            new(newOid, "Imported Alex", ClassMembershipRole.Member),
            new(existingStudent.EntraOid, existingStudent.DisplayName, ClassMembershipRole.Member),
            new(Guid.Empty, "Bad Row", ClassMembershipRole.Member),
            new(Guid.NewGuid(), null, ClassMembershipRole.Member),
        };

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var response = await client.PostAsJsonAsync(
            $"/classes/{scenario.Class.Id}/members/import",
            new ImportClassMembersRequest(rows));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ImportClassMembersResponse>();
        Assert.NotNull(body);
        Assert.Equal(4, body!.Results.Count);
        Assert.Equal(ClassMembershipImportStatus.Added, body.Results[0].Status);
        Assert.Equal(ClassMembershipImportStatus.AlreadyMember, body.Results[1].Status);
        Assert.Equal(ClassMembershipImportStatus.NotFoundInEntra, body.Results[2].Status);
        Assert.Equal(ClassMembershipImportStatus.NotFoundInEntra, body.Results[3].Status);

        var roster = await client.GetFromJsonAsync<ClassMembersResponse>($"/classes/{scenario.Class.Id}/members");
        Assert.Contains(roster!.Members, m => m.EntraOid == newOid);
    }

    [Fact]
    public async Task POST_class_members_import_rejects_too_many_rows()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var rows = Enumerable.Range(0, ClassesController.MaxImportRows + 1)
            .Select(_ => new ImportClassMemberRow(Guid.NewGuid(), "x", ClassMembershipRole.Member))
            .ToList();

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var response = await client.PostAsJsonAsync(
            $"/classes/{scenario.Class.Id}/members/import",
            new ImportClassMembersRequest(rows));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task POST_class_members_import_as_non_teacher_of_class_returns_403()
    {
        var owned = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var other = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, owned.Teacher);

        var response = await client.PostAsJsonAsync(
            $"/classes/{other.Class.Id}/members/import",
            new ImportClassMembersRequest(new List<ImportClassMemberRow>
            {
                new(Guid.NewGuid(), "Nope", ClassMembershipRole.Member),
            }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
