using FocusAgent.Core.Auth;
using FocusAgent.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FocusAgent.App.Auth;

/// <summary>
/// Dev-only token provider that bypasses WAM entirely. Returns an empty
/// access token so SignalR sends no <c>Authorization</c> header, leaving the
/// backend to authenticate the connection via the
/// <c>X-Dev-Impersonate-Oid</c> header (handled server-side by
/// <c>DevImpersonationAuthHandler</c>, added in #44).
///
/// Registered in place of <see cref="WamTokenProvider"/> when the agent is
/// launched with the <c>--inject-token</c> flag (see
/// <c>Program.InjectToken</c>). Requires <c>Dev:ImpersonateOid</c> to be set
/// — without it the agent has no way to identify itself to the backend at
/// all and would just spin in retry loops.
///
/// This is the unlock for headless verification: scripts and the verify
/// runner can launch the agent without ever needing an interactive WAM
/// picker, which was the structural blocker on the #41 debug cycle.
/// </summary>
public sealed class InjectedTokenProvider : IAuthTokenProvider
{
    private readonly string _impersonateOid;
    private readonly ILogger<InjectedTokenProvider> _log;

    public InjectedTokenProvider(
        IOptions<DevSettings> dev,
        ILogger<InjectedTokenProvider> log)
    {
        _log = log;
        var oid = dev.Value.ImpersonateOid?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(oid))
        {
            throw new InvalidOperationException(
                "--inject-token requires Dev:ImpersonateOid to be set (typically in agent appsettings.Development.json). " +
                "Without an impersonation OID the agent has no identity to send to the backend.");
        }
        _impersonateOid = oid;
        _log.LogWarning(
            "WAM bypass active (--inject-token). Hub will authenticate via X-Dev-Impersonate-Oid={Oid} only. " +
            "Never enable this in production.",
            _impersonateOid);
    }

    public Task<string> GetAccessTokenAsync(CancellationToken ct = default) =>
        // Empty string -> SignalR won't attach an Authorization header. The
        // backend's DevImpersonation scheme picks the request up via the
        // impersonation header instead.
        Task.FromResult(string.Empty);

    public Task<AuthResult> AcquireTokenAsync(CancellationToken ct = default) =>
        Task.FromResult(new AuthResult(
            AccessToken: string.Empty,
            Username: $"impersonated:{_impersonateOid}",
            DisplayName: "Dev Impersonated",
            ExpiresOn: DateTimeOffset.MaxValue));
}
