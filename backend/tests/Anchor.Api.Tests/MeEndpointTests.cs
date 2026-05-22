using System.Net;
using System.Net.Http.Json;
using Anchor.Api.Controllers;
using Anchor.Api.Tests.FakeAuth;
using Anchor.Domain.Users;
using Microsoft.Extensions.DependencyInjection;

namespace Anchor.Api.Tests;

public sealed class MeEndpointTests : IClassFixture<AnchorApiFactory>
{
    private readonly AnchorApiFactory _factory;

    public MeEndpointTests(AnchorApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Unauthenticated_request_returns_401()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Teacher_token_returns_200_with_teacher_role()
    {
        using var client = _factory.CreateClient();
        var oid = Guid.NewGuid();
        AddFakeAuth(client, oid, "Teacher", "Alice Teacher");

        var response = await client.GetAsync("/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<MeResponse>();
        Assert.NotNull(body);
        Assert.Equal(oid, body!.EntraOid);
        Assert.Equal(UserRole.Teacher, body.Role);
        Assert.Equal("Alice Teacher", body.DisplayName);
    }

    [Fact]
    public async Task Student_token_returns_200_with_student_role()
    {
        using var client = _factory.CreateClient();
        var oid = Guid.NewGuid();
        AddFakeAuth(client, oid, "Student", "Bob Student");

        var response = await client.GetAsync("/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<MeResponse>();
        Assert.NotNull(body);
        Assert.Equal(oid, body!.EntraOid);
        Assert.Equal(UserRole.Student, body.Role);
        Assert.Equal("Bob Student", body.DisplayName);
    }

    [Fact]
    public async Task First_sign_in_persists_user_with_entra_oid()
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IUserStore>();

        using var client = _factory.CreateClient();
        var oid = Guid.NewGuid();
        AddFakeAuth(client, oid, "Student", "Carol New");

        Assert.Null(await store.FindByEntraOidAsync(oid));

        var response = await client.GetAsync("/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var persisted = await store.FindByEntraOidAsync(oid);
        Assert.NotNull(persisted);
        Assert.Equal(oid, persisted!.EntraOid);
        Assert.Equal("Carol New", persisted.DisplayName);
        Assert.Equal(UserRole.Student, persisted.Role);
    }

    [Fact]
    public async Task Subsequent_sign_in_returns_same_user_id()
    {
        using var client = _factory.CreateClient();
        var oid = Guid.NewGuid();
        AddFakeAuth(client, oid, "Teacher", "Dave Repeat");

        var first = await client.GetFromJsonAsync<MeResponse>("/me");
        var second = await client.GetFromJsonAsync<MeResponse>("/me");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.Id, second!.Id);
    }

    [Fact]
    public async Task Token_without_known_role_returns_403()
    {
        using var client = _factory.CreateClient();
        var oid = Guid.NewGuid();
        AddFakeAuth(client, oid, role: "", "No Role");

        var response = await client.GetAsync("/me");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static void AddFakeAuth(HttpClient client, Guid oid, string role, string name)
    {
        client.DefaultRequestHeaders.Remove(FakeJwtBearerHandler.HeaderOid);
        client.DefaultRequestHeaders.Remove(FakeJwtBearerHandler.HeaderRole);
        client.DefaultRequestHeaders.Remove(FakeJwtBearerHandler.HeaderName);
        client.DefaultRequestHeaders.Add(FakeJwtBearerHandler.HeaderOid, oid.ToString());
        if (!string.IsNullOrEmpty(role))
            client.DefaultRequestHeaders.Add(FakeJwtBearerHandler.HeaderRole, role);
        client.DefaultRequestHeaders.Add(FakeJwtBearerHandler.HeaderName, name);
    }
}
