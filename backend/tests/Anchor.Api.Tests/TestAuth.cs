using Anchor.Api.Tests.FakeAuth;
using Anchor.Domain.Users;

namespace Anchor.Api.Tests;

internal static class TestAuth
{
    public static void Set(HttpClient client, User user, string role)
    {
        client.DefaultRequestHeaders.Remove(FakeJwtBearerHandler.HeaderOid);
        client.DefaultRequestHeaders.Remove(FakeJwtBearerHandler.HeaderRole);
        client.DefaultRequestHeaders.Remove(FakeJwtBearerHandler.HeaderName);
        client.DefaultRequestHeaders.Add(FakeJwtBearerHandler.HeaderOid, user.EntraOid.ToString());
        if (!string.IsNullOrEmpty(role))
            client.DefaultRequestHeaders.Add(FakeJwtBearerHandler.HeaderRole, role);
        client.DefaultRequestHeaders.Add(FakeJwtBearerHandler.HeaderName, user.DisplayName);
    }

    public static void SetTeacher(HttpClient client, User user) => Set(client, user, "Teacher");
    public static void SetStudent(HttpClient client, User user) => Set(client, user, "Student");
    public static void SetAdmin(HttpClient client, User user) => Set(client, user, "Admin");
}
