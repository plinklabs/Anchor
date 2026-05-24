using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FocusAgent.Core.Auth;
using FocusAgent.Core.Dtos;
using FocusAgent.Core.Sessions;
using FocusAgent.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FocusAgent.App.Sessions;

/// <summary>
/// Real implementation of <see cref="ISessionRehydrationClient"/> that talks
/// to the backend's <c>GET /sessions/rejoinable</c> endpoint (#54). As of
/// #70 that endpoint returns a list of <see cref="SessionStartedPayload"/>
/// (with the expanded allowlist) so a post-restart rejoin carries the same
/// rules a fresh broadcast would — no local disk cache.
///
/// Auth mirrors <see cref="Realtime.SignalRSessionHubConnection"/>: bearer
/// token from <see cref="IAuthTokenProvider"/>, plus the dev-only
/// <c>X-Dev-Impersonate-Oid</c> header when <see cref="DevSettings.ImpersonateOid"/>
/// is set. The header overrides the bearer's <c>oid</c> claim server-side
/// (only in Development), which is how the headless verifier and #44 dev path
/// authenticate without a real WAM token.
/// </summary>
public sealed class HttpSessionRehydrationClient : ISessionRehydrationClient
{
    private const string DevImpersonateOidHeader = "X-Dev-Impersonate-Oid";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly IAuthTokenProvider _tokens;
    private readonly DevSettings _dev;
    private readonly ILogger<HttpSessionRehydrationClient> _log;

    public HttpSessionRehydrationClient(
        HttpClient http,
        IAuthTokenProvider tokens,
        IOptions<BackendSettings> backend,
        IOptions<DevSettings> dev,
        ILogger<HttpSessionRehydrationClient> log)
    {
        _http = http;
        _tokens = tokens;
        _dev = dev.Value;
        _log = log;

        var baseUrl = backend.Value.BaseUrl.TrimEnd('/');
        _http.BaseAddress = new Uri(baseUrl + "/");
        _http.Timeout = TimeSpan.FromSeconds(10);
    }

    public async Task<IReadOnlyList<SessionStartedPayload>> GetRejoinableSessionsAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "sessions/rejoinable");

        var token = await _tokens.GetAccessTokenAsync().ConfigureAwait(false);
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (!string.IsNullOrWhiteSpace(_dev.ImpersonateOid))
        {
            request.Headers.Add(DevImpersonateOidHeader, _dev.ImpersonateOid);
            _log.LogDebug("Rehydration request using impersonation OID {Oid}", _dev.ImpersonateOid);
        }

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var payloads = await response.Content
            .ReadFromJsonAsync<List<SessionStartedPayload>>(JsonOptions, ct)
            .ConfigureAwait(false);
        return (IReadOnlyList<SessionStartedPayload>?)payloads ?? Array.Empty<SessionStartedPayload>();
    }
}
