using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// REST client the harness uses to drive session lifecycle against the real
/// backend, exactly the way the dashboard would — except auth is the dev
/// impersonation header (X-Dev-Impersonate-Oid) the backend honours in a
/// Development build (Anchor.Api.Auth.DevImpersonationAuthHandler). No token
/// acquisition, no UI: the wire contract is the backend, so we talk to it
/// directly and let SignalR carry the push to the agent. Mirrors
/// extension/e2e/backend.ts.
/// </summary>
internal sealed class BackendClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public BackendClient(string baseUrl)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(10) };
    }

    public sealed record StartedSession(Guid Id, string JoinCode, Guid ClassId);

    /// <summary>Resolve the seeded class ("3A") to its id, as the seeded teacher.</summary>
    public async Task<Guid> FindClassIdAsync(string name = TestConfig.ClassName, string oid = TestConfig.TeacherOid)
    {
        var classes = await GetJsonAsync<List<NamedRef>>("/classes", oid);
        var match = classes.FirstOrDefault(c => c.Name == name)
            ?? throw new InvalidOperationException(
                $"Class '{name}' not found via /classes (got: {Names(classes)}). Did the dev seeder run?");
        return match.Id;
    }

    /// <summary>Resolve a bundle ("Notepad (dev)") to its id.</summary>
    public async Task<Guid> FindBundleIdAsync(string name, string oid = TestConfig.TeacherOid)
    {
        var bundles = await GetJsonAsync<List<NamedRef>>("/bundles", oid);
        var match = bundles.FirstOrDefault(b => b.Name == name)
            ?? throw new InvalidOperationException(
                $"Bundle '{name}' not found via /bundles (got: {Names(bundles)}).");
        return match.Id;
    }

    /// <summary>POST /sessions — start a session for a class with the given bundles.</summary>
    public async Task<StartedSession> StartSessionAsync(
        Guid classId, IEnumerable<Guid>? bundleIds = null, string oid = TestConfig.TeacherOid)
    {
        using var res = await SendAsync(HttpMethod.Post, "/sessions", oid,
            new { classId, bundleIds = bundleIds ?? Array.Empty<Guid>() });
        return await ReadAsync<StartedSession>(res);
    }

    /// <summary>POST /sessions/{id}/end — end a running session.</summary>
    public async Task EndSessionAsync(Guid sessionId, string oid = TestConfig.TeacherOid)
    {
        using var res = await SendAsync(HttpMethod.Post, $"/sessions/{sessionId}/end", oid);
        _ = res;
    }

    /// <summary>
    /// PUT /sessions/{id}/bundles — replace the session's bundle set mid-session.
    /// The backend pushes SessionBundlesUpdated to each actively-joined student,
    /// which is what the agent rebuilds its matcher against (#93).
    /// </summary>
    public async Task UpdateBundlesAsync(Guid sessionId, IEnumerable<Guid> bundleIds, string oid = TestConfig.TeacherOid)
    {
        using var res = await SendAsync(HttpMethod.Put, $"/sessions/{sessionId}/bundles", oid,
            new { bundleIds });
        _ = res;
    }

    /// <summary>
    /// POST /sessions/join-by-code — join an arbitrary running session by code,
    /// the only path an unrostered outsider can get in (#34). Returns the raw
    /// response so error-path specs can assert 404 / 429 without an exception.
    /// </summary>
    public Task<HttpResponseMessage> JoinByCodeRawAsync(string code, string oid)
        => SendAsync(HttpMethod.Post, "/sessions/join-by-code", oid, new { code });

    public async Task<Guid> JoinByCodeAsync(string code, string oid)
    {
        using var res = await JoinByCodeRawAsync(code, oid);
        res.EnsureSuccessStatusCode();
        var body = await ReadAsync<JoinByCodeResult>(res);
        return body.SessionId;
    }

    /// <summary>
    /// GET /sessions/{id} → the recentEvents kinds. EventKind serializes as an
    /// int; map it back to the symbolic names the specs assert on (mirrors the
    /// HeartbeatVerifier dev tool).
    /// </summary>
    public async Task<List<string>> GetSessionEventKindsAsync(Guid sessionId, string oid = TestConfig.TeacherOid)
    {
        using var res = await SendAsync(HttpMethod.Get, $"/sessions/{sessionId}", oid);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var kinds = new List<string>();
        if (doc.RootElement.TryGetProperty("recentEvents", out var events))
        {
            foreach (var e in events.EnumerateArray())
            {
                var kindElem = e.GetProperty("kind");
                kinds.Add(kindElem.ValueKind == JsonValueKind.Number
                    ? kindElem.GetInt32() switch
                    {
                        0 => "ForegroundChange",
                        1 => "BlockedUrl",
                        2 => "UnblockRequest",
                        3 => "HeartbeatLost",
                        4 => "AgentReconnected",
                        5 => "AgentKilled",
                        6 => "ManualLeave",
                        7 => "JoinDeclined",
                        var n => $"Unknown({n})",
                    }
                    : kindElem.GetString() ?? "");
            }
        }
        return kinds;
    }

    private async Task<T> GetJsonAsync<T>(string path, string oid)
    {
        using var res = await SendAsync(HttpMethod.Get, path, oid);
        return await ReadAsync<T>(res);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string oid, object? body = null)
    {
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Add("X-Dev-Impersonate-Oid", oid);
        if (body is not null)
            req.Content = JsonContent.Create(body, options: Json);

        var res = await _http.SendAsync(req);
        // join-by-code error paths are inspected by the caller; let those bubble
        // up with the response intact. Everything else must succeed.
        if (!res.IsSuccessStatusCode && path != "/sessions/join-by-code")
        {
            var status = res.StatusCode;
            var text = await res.Content.ReadAsStringAsync();
            res.Dispose();
            throw new HttpRequestException($"{method} {path} → {(int)status} {status}: {text}");
        }
        return res;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage res)
        => (await res.Content.ReadFromJsonAsync<T>(Json))
           ?? throw new InvalidOperationException($"{res.RequestMessage?.RequestUri} returned null body.");

    private static string Names(IEnumerable<NamedRef> refs)
    {
        var joined = string.Join(", ", refs.Select(r => r.Name));
        return joined.Length == 0 ? "<none>" : joined;
    }

    private sealed record NamedRef(Guid Id, string Name);
    private sealed record JoinByCodeResult(Guid SessionId);
}
