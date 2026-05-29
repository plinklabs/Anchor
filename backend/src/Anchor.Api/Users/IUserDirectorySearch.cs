namespace Anchor.Api.Users;

public interface IUserDirectorySearch
{
    Task<IReadOnlyList<DirectoryUser>> SearchAsync(
        string query,
        int top,
        CancellationToken cancellationToken);
}

public sealed record DirectoryUser(Guid EntraOid, string DisplayName, string? Upn);
