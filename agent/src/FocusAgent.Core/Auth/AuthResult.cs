namespace FocusAgent.Core.Auth;

/// <param name="AccountId">
/// The signed-in account's Entra object ID (the token's <c>oid</c>), when known:
/// what an Edge profile signed in with the same account carries as its
/// <c>edge_account_oid</c> (#525).
/// </param>
public sealed record AuthResult(
    string AccessToken,
    string Username,
    string DisplayName,
    DateTimeOffset ExpiresOn,
    string? AccountId = null);
