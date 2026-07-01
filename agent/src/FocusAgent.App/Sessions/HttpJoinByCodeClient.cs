using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FocusAgent.App.Localization;
using FocusAgent.Core.Auth;
using FocusAgent.Core.Sessions;
using FocusAgent.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FocusAgent.App.Sessions;

/// <summary>
/// HttpClient-backed <see cref="IJoinByCodeClient"/> for the manual
/// <c>POST /sessions/join-by-code</c> path (#34). Auth mirrors
/// <see cref="HttpSessionRehydrationClient"/>: bearer token from
/// <see cref="IAuthTokenProvider"/>, plus the dev-only
/// <c>X-Dev-Impersonate-Oid</c> header when <see cref="DevSettings.ImpersonateOid"/>
/// is set so headless verify scripts can run without a real WAM token.
/// </summary>
public sealed class HttpJoinByCodeClient : IJoinByCodeClient
{
    private const string DevImpersonateOidHeader = "X-Dev-Impersonate-Oid";

    private static readonly JsonSerializerOptions ErrorReadOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly IAuthTokenProvider _tokens;
    private readonly DevSettings _dev;
    private readonly ILogger<HttpJoinByCodeClient> _log;

    public HttpJoinByCodeClient(
        HttpClient http,
        IAuthTokenProvider tokens,
        IOptions<BackendSettings> backend,
        IOptions<DevSettings> dev,
        ILogger<HttpJoinByCodeClient> log)
    {
        _http = http;
        _tokens = tokens;
        _dev = dev.Value;
        _log = log;

        var baseUrl = backend.Value.BaseUrl.TrimEnd('/');
        _http.BaseAddress = new Uri(baseUrl + "/");
        _http.Timeout = TimeSpan.FromSeconds(10);
    }

    public async Task<JoinByCodeOutcome> JoinAsync(string code, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "sessions/join-by-code")
        {
            Content = JsonContent.Create(new { code }),
        };

        try
        {
            var token = await _tokens.GetAccessTokenAsync().ConfigureAwait(false);
            if (!string.IsNullOrEmpty(token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to acquire access token for join-by-code; sending without bearer.");
        }

        if (!string.IsNullOrWhiteSpace(_dev.ImpersonateOid))
            request.Headers.Add(DevImpersonateOidHeader, _dev.ImpersonateOid);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Network error calling /sessions/join-by-code.");
            return new JoinByCodeOutcome(JoinByCodeStatus.NetworkError, Loc.Get("JoinError_Network"));
        }

        try
        {
            return await MapAsync(response, ct).ConfigureAwait(false);
        }
        finally
        {
            response.Dispose();
        }
    }

    private static async Task<JoinByCodeOutcome> MapAsync(HttpResponseMessage response, CancellationToken ct)
    {
        switch (response.StatusCode)
        {
            case HttpStatusCode.OK:
                return new JoinByCodeOutcome(JoinByCodeStatus.Success, "");
            case HttpStatusCode.Unauthorized:
                return new JoinByCodeOutcome(JoinByCodeStatus.Unauthorized,
                    Loc.Get("JoinError_Unauthorized"));
            case HttpStatusCode.NotFound:
                return new JoinByCodeOutcome(JoinByCodeStatus.NotFound,
                    await ReadMessageOrDefaultAsync(response, Loc.Get("JoinError_NotFound"), ct).ConfigureAwait(false));
            case HttpStatusCode.Conflict:
                return new JoinByCodeOutcome(JoinByCodeStatus.AlreadyInSession,
                    await ReadMessageOrDefaultAsync(response, Loc.Get("JoinError_AlreadyInSession"), ct).ConfigureAwait(false));
            case HttpStatusCode.Gone:
                return new JoinByCodeOutcome(JoinByCodeStatus.Expired,
                    await ReadMessageOrDefaultAsync(response, Loc.Get("JoinError_Expired"), ct).ConfigureAwait(false));
            case HttpStatusCode.TooManyRequests:
                return new JoinByCodeOutcome(JoinByCodeStatus.RateLimited,
                    await ReadMessageOrDefaultAsync(response, Loc.Get("JoinError_RateLimited"), ct).ConfigureAwait(false));
            default:
                return new JoinByCodeOutcome(JoinByCodeStatus.NetworkError,
                    Loc.Format("JoinError_Unexpected", (int)response.StatusCode));
        }
    }

    private static async Task<string> ReadMessageOrDefaultAsync(HttpResponseMessage response, string fallback, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
                return fallback;
            var parsed = JsonSerializer.Deserialize<ErrorBody>(body, ErrorReadOptions);
            return string.IsNullOrWhiteSpace(parsed?.Message) ? fallback : parsed!.Message!;
        }
        catch
        {
            return fallback;
        }
    }

    private sealed record ErrorBody([property: JsonPropertyName("message")] string? Message);
}
