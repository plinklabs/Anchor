using System.Security.Claims;

namespace Anchor.Api;

public static class PrincipalExtensions
{
    private const string EntraOidShortClaim = "oid";
    private const string EntraOidLongClaim = "http://schemas.microsoft.com/identity/claims/objectidentifier";

    public static bool TryGetEntraOid(this ClaimsPrincipal principal, out Guid entraOid)
    {
        var value = principal.FindFirst(EntraOidShortClaim)?.Value
                    ?? principal.FindFirst(EntraOidLongClaim)?.Value;
        return Guid.TryParse(value, out entraOid);
    }
}
