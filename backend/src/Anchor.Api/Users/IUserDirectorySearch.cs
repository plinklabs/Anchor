namespace Anchor.Api.Users;

public interface IUserDirectorySearch
{
    Task<IReadOnlyList<DirectoryUser>> SearchAsync(
        string query,
        int top,
        CancellationToken cancellationToken);

    /// Resolves a single userPrincipalName to a directory user. Returns null
    /// when the UPN does not exist (or is malformed); throws for systemic
    /// directory failures (auth/consent, throttling, outage).
    Task<DirectoryUser?> ResolveByUpnAsync(
        string upn,
        CancellationToken cancellationToken);
}

public sealed record DirectoryUser(Guid EntraOid, string DisplayName, string? Upn);
