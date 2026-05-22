using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Anchor.Api.Tests.FakeAuth;

public sealed class FakeJwtBearerHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Test";

    public const string HeaderOid = "X-Test-OID";
    public const string HeaderRole = "X-Test-Role";
    public const string HeaderName = "X-Test-Name";

    public FakeJwtBearerHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderOid, out var oid) || string.IsNullOrWhiteSpace(oid))
            return Task.FromResult(AuthenticateResult.NoResult());

        var role = Request.Headers[HeaderRole].ToString();
        var name = Request.Headers[HeaderName].ToString();

        var claims = new List<Claim>
        {
            new("oid", oid.ToString()),
            new("name", string.IsNullOrWhiteSpace(name) ? "Test User" : name),
        };
        if (!string.IsNullOrWhiteSpace(role))
            claims.Add(new Claim("roles", role));

        var identity = new ClaimsIdentity(claims, SchemeName, nameType: "name", roleType: "roles");
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
