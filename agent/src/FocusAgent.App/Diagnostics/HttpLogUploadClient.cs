using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FocusAgent.Core.Auth;
using FocusAgent.Core.Diagnostics;
using FocusAgent.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FocusAgent.App.Diagnostics;

/// <summary>
/// The real <see cref="ILogUploadClient"/> (#446): <c>GET /log-uploads/pending</c>
/// and <c>POST /log-uploads/{sessionId}</c> on the backend. Auth mirrors
/// <see cref="Sessions.HttpSessionRehydrationClient"/>: the bearer token from
/// <see cref="IAuthTokenProvider"/>, plus the dev-only <c>X-Dev-Impersonate-Oid</c>
/// header when <see cref="DevSettings.ImpersonateOid"/> is set.
/// </summary>
public sealed class HttpLogUploadClient : ILogUploadClient
{
    private const string DevImpersonateOidHeader = "X-Dev-Impersonate-Oid";
    private const string GzipContentType = "application/gzip";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly IAuthTokenProvider _tokens;
    private readonly DevSettings _dev;
    private readonly ILogger<HttpLogUploadClient> _log;

    public HttpLogUploadClient(
        HttpClient http,
        IAuthTokenProvider tokens,
        IOptions<BackendSettings> backend,
        IOptions<DevSettings> dev,
        ILogger<HttpLogUploadClient> log)
    {
        _http = http;
        _tokens = tokens;
        _dev = dev.Value;
        _log = log;

        _http.BaseAddress = new Uri(backend.Value.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<PendingLogWindows> GetPendingAsync(CancellationToken ct = default)
    {
        using var request = await RequestAsync(HttpMethod.Get, "log-uploads/pending").ConfigureAwait(false);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var pending = await response.Content
            .ReadFromJsonAsync<PendingDto>(JsonOptions, ct)
            .ConfigureAwait(false);
        var windows = (pending?.Windows ?? [])
            .Select(w => new LogWindow(w.SessionId, w.From, w.To))
            .ToList();
        return new PendingLogWindows(pending?.MaxBytes ?? 0, windows);
    }

    public async Task<LogUploadOutcome> UploadAsync(
        LogWindow window, string agentVersion, byte[] gzip, CancellationToken ct = default)
    {
        var path = $"log-uploads/{window.SessionId}"
                   + $"?to={Uri.EscapeDataString(window.To.ToString("O", CultureInfo.InvariantCulture))}"
                   + $"&agentVersion={Uri.EscapeDataString(agentVersion)}";
        using var request = await RequestAsync(HttpMethod.Post, path).ConfigureAwait(false);
        request.Content = new ByteArrayContent(gzip);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(GzipContentType);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        switch (response.StatusCode)
        {
            case HttpStatusCode.NoContent:
            case HttpStatusCode.OK:
            case HttpStatusCode.Created:
                return LogUploadOutcome.Stored;
            case HttpStatusCode.Conflict:
                return LogUploadOutcome.AlreadyComplete;
            case HttpStatusCode.NotFound:
            case HttpStatusCode.BadRequest:
            case HttpStatusCode.RequestEntityTooLarge:
                _log.LogWarning("Log upload of session {SessionId} refused: {Status}.",
                    window.SessionId, (int)response.StatusCode);
                return LogUploadOutcome.Refused;
            default:
                response.EnsureSuccessStatusCode();
                return LogUploadOutcome.Stored;
        }
    }

    private async Task<HttpRequestMessage> RequestAsync(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        var token = await _tokens.GetAccessTokenAsync().ConfigureAwait(false);
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (!string.IsNullOrWhiteSpace(_dev.ImpersonateOid))
            request.Headers.Add(DevImpersonateOidHeader, _dev.ImpersonateOid);
        return request;
    }

    private sealed record PendingDto(int MaxBytes, List<WindowDto>? Windows);

    private sealed record WindowDto(Guid SessionId, DateTimeOffset From, DateTimeOffset To);
}
