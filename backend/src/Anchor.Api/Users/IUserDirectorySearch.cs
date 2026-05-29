namespace Anchor.Api.Users;

public interface IUserDirectorySearch
{
    /// Free-text search for users by display name or UPN, optionally scoped to
    /// a single school via the Entra <c>companyName</c> attribute. The scope
    /// matters: class codes are not unique across the Arcadia group (#96), so
    /// the roster screen always passes a company.
    Task<IReadOnlyList<DirectoryUser>> SearchAsync(
        string query,
        int top,
        string? company,
        CancellationToken cancellationToken);

    /// Resolves a single userPrincipalName to a directory user. Returns null
    /// when the UPN does not exist (or is malformed); throws for systemic
    /// directory failures (auth/consent, throttling, outage). The returned
    /// record carries the user's company + department so callers can decide
    /// whether the row matches the import scope (e.g. <see
    /// cref="DirectoryUser.Company"/> = selected school).
    Task<DirectoryUser?> ResolveByUpnAsync(
        string upn,
        CancellationToken cancellationToken);

    /// Lists all users in <paramref name="company"/> whose <c>department</c>
    /// matches <paramref name="classCode"/>. Used to populate a roster in one
    /// shot from the Entra-side class membership (#96 bulk import). Caller is
    /// responsible for not retaining more than <paramref name="top"/> rows.
    Task<IReadOnlyList<DirectoryUser>> ListByClassAsync(
        string company,
        string classCode,
        int top,
        CancellationToken cancellationToken);

    /// Returns the distinct, non-empty <c>companyName</c> values seen in the
    /// directory. Used to populate the school selector on the roster screen
    /// (#96). May be a heavy call — Graph has no native DISTINCT — so callers
    /// should cache the result for the duration of a UI session.
    Task<IReadOnlyList<string>> ListCompaniesAsync(CancellationToken cancellationToken);
}

public sealed record DirectoryUser(
    Guid EntraOid,
    string DisplayName,
    string? Upn,
    string? Company,
    string? Department);
