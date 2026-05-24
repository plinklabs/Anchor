using System.Security.Claims;
using System.Text.Encodings.Web;
using Anchor.Domain.Users;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Anchor.Api.Auth;

/// <summary>
/// Dev-only authentication scheme that accepts the
/// <c>X-Dev-Impersonate-Oid</c> request header (or the
/// <c>dev_impersonate_oid</c> query string on the SignalR hub path) as
/// proof of identity. Resolves the OID against the user store and emits an
/// authenticated principal whose <c>oid</c>, <c>name</c>, and <c>roles</c>
/// claims mirror what a real Entra JWT would carry. Registered only when
/// <see cref="IHostEnvironment.IsDevelopment"/> is true.
///
/// Counterpart of the existing hub-side override
/// (<see cref="Realtime.SessionHub.DevImpersonateOidHeader"/>): the hub already
/// recognises this header on its negotiate request, but REST controllers used
/// to require a real bearer token, which forced human-in-the-loop sign-in for
/// every verification cycle. With this scheme registered as a secondary
/// authentication option, headless callers (the agent in --inject-token mode,
/// dev scripts, the verify-session-start.ps1 runner) can POST /sessions etc.
/// as a seeded teacher without acquiring a real Entra token.
///
/// The query-string fallback exists because browser-hosted SignalR clients
/// (the Edge extension, #72) cannot attach custom headers to the WebSocket
/// upgrade — only the URL travels intact. Restricted to the hub path so the
/// REST surface keeps the header-only contract.
///
/// The handler returns <see cref="AuthenticateResult.NoResult"/> when neither
/// signal is present so JwtBearer remains the canonical path for real callers.
/// </summary>
public sealed class DevImpersonationAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "DevImpersonation";
    public const string HeaderName = "X-Dev-Impersonate-Oid";
    public const string QueryParamName = "dev_impersonate_oid";

    private readonly IUserStore _users;

    public DevImpersonationAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IUserStore users)
        : base(options, logger, encoder)
    {
        _users = users;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var raw = Request.Headers[HeaderName].ToString();
        if (string.IsNullOrWhiteSpace(raw) && Request.Path.StartsWithSegments(Realtime.SessionHub.Path))
            raw = Request.Query[QueryParamName].ToString();

        if (string.IsNullOrWhiteSpace(raw))
            return AuthenticateResult.NoResult();

        if (!Guid.TryParse(raw, out var oid))
            return AuthenticateResult.Fail($"Dev impersonation value '{raw}' is not a valid GUID.");

        var user = await _users.FindByEntraOidAsync(oid);
        if (user is null)
            return AuthenticateResult.Fail($"Impersonation OID {oid} is not provisioned in the user store.");

        var role = user.Role switch
        {
            UserRole.Teacher => "Teacher",
            UserRole.Student => "Student",
            UserRole.Admin => "Admin",
            _ => string.Empty,
        };

        var claims = new List<Claim>
        {
            new("oid", oid.ToString()),
            new("name", user.DisplayName),
        };
        if (!string.IsNullOrEmpty(role))
            claims.Add(new Claim("roles", role));

        var identity = new ClaimsIdentity(claims, SchemeName, nameType: "name", roleType: "roles");
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return AuthenticateResult.Success(ticket);
    }
}
