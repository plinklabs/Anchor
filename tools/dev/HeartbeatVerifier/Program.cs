using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;

// Headless end-to-end verifier for the heartbeat ping feature (issue #32).
//
// Asserts the full chain with a real backend + real SignalR + real wall time
// (sped up via Heartbeat:* config on the backend's appsettings.Development.json
// — see scripts/dev/verify-heartbeat.ps1 for the wrapper that patches it):
//
//   1. POST /sessions as the seeded Dev Teacher.
//   2. Open a SignalR connection as the seeded Dev Student.
//   3. JoinSession, then invoke Heartbeat() three times.
//   4. Stop pinging, wait > 2 × interval + scan: assert a HeartbeatLost
//      event lands in the session's /sessions/{id} recent events.
//   5. Resume pinging, wait > scan: assert an AgentReconnected event lands.
//   6. POST /sessions/{id}/end.

const string BackendUrlFlag = "--backend";
const string IntervalFlag = "--interval-seconds";
const string TeacherOidFlag = "--teacher-oid";
const string StudentOidFlag = "--student-oid";
const string ClassNameFlag = "--class-name";

var backendUrl = ArgOr(args, BackendUrlFlag, "http://localhost:5276");
var intervalSeconds = int.Parse(ArgOr(args, IntervalFlag, "2"));
var teacherOid = ArgOr(args, TeacherOidFlag, "11111111-1111-1111-1111-111111111111");
var studentOid = ArgOr(args, StudentOidFlag, "22222222-2222-2222-2222-222222222222");
var className = ArgOr(args, ClassNameFlag, "3A");

// The script that wraps this binary configures the backend's
// HeartbeatOptions to interval=intervalSeconds, multiplier=2, scan=1.
// Stale-detection lag is therefore (2 × interval) + scan + small slop.
var staleWait = TimeSpan.FromSeconds(intervalSeconds * 2 + 2);
var recoverWait = TimeSpan.FromSeconds(3);

using var http = new HttpClient { BaseAddress = new Uri(backendUrl), Timeout = TimeSpan.FromSeconds(10) };
http.DefaultRequestHeaders.Add("X-Dev-Impersonate-Oid", teacherOid);

Log($"backend={backendUrl} interval={intervalSeconds}s teacher={teacherOid} student={studentOid}");

Log("Resolving class id…");
var classes = await http.GetFromJsonAsync<List<ClassDto>>("/classes")
    ?? throw new InvalidOperationException("/classes returned null");
var target = classes.FirstOrDefault(c => c.Name == className)
    ?? throw new InvalidOperationException($"Class '{className}' not found.");

Log($"POST /sessions for class {target.Id}…");
var createResp = await http.PostAsJsonAsync("/sessions", new { classId = target.Id, mode = "Strict", bundleIds = Array.Empty<Guid>() });
createResp.EnsureSuccessStatusCode();
var session = await createResp.Content.ReadFromJsonAsync<SessionDto>()
    ?? throw new InvalidOperationException("/sessions returned null");
Log($"Session created: {session.Id}");

Log("Opening SignalR connection as Dev Student…");
var conn = new HubConnectionBuilder()
    .WithUrl(new Uri($"{backendUrl}/hubs/session"), opts =>
    {
        opts.Headers["X-Dev-Impersonate-Oid"] = studentOid;
    })
    .Build();
await conn.StartAsync();

Log("JoinSession…");
await conn.InvokeAsync("JoinSession", new { SessionId = session.Id, JoinCode = (string?)null });

Log($"Sending 3 Heartbeats at {intervalSeconds}s cadence…");
for (var i = 0; i < 3; i++)
{
    await conn.InvokeAsync("Heartbeat", session.Id);
    await Task.Delay(TimeSpan.FromSeconds(intervalSeconds));
}

// No HeartbeatLost / AgentReconnected expected yet — agent has been pinging
// continuously.
var quietEvents = await GetSessionEventsAsync(http, session.Id);
RequireZero(quietEvents, "HeartbeatLost", "while pinging continuously");
RequireZero(quietEvents, "AgentReconnected", "while pinging continuously");
Log("PASS: no transition events while pinging continuously.");

Log($"Stopping pings, waiting {staleWait.TotalSeconds}s for HeartbeatLost…");
await Task.Delay(staleWait);

var lostEvents = await GetSessionEventsAsync(http, session.Id);
var lost = lostEvents.Where(e => e.Kind == "HeartbeatLost").ToList();
if (lost.Count != 1)
{
    Fail($"expected exactly 1 HeartbeatLost after outage, got {lost.Count}");
    return await EndAndExit(http, conn, session.Id, 2);
}
Log($"PASS: HeartbeatLost emitted once (payload: {lost[0].PayloadJson}).");

Log("Resuming heartbeats…");
await conn.InvokeAsync("Heartbeat", session.Id);
await Task.Delay(recoverWait);

var afterResume = await GetSessionEventsAsync(http, session.Id);
var reconnected = afterResume.Where(e => e.Kind == "AgentReconnected").ToList();
if (reconnected.Count != 1)
{
    Fail($"expected exactly 1 AgentReconnected after resume, got {reconnected.Count}");
    return await EndAndExit(http, conn, session.Id, 3);
}
Log($"PASS: AgentReconnected emitted once (payload: {reconnected[0].PayloadJson}).");

return await EndAndExit(http, conn, session.Id, 0);

static string ArgOr(string[] args, string flag, string fallback)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == flag) return args[i + 1];
    }
    return fallback;
}

static void Log(string msg) =>
    Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] {msg}");

static void Fail(string msg)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"[FAIL] {msg}");
    Console.ResetColor();
}

static void RequireZero(List<EventDto> events, string kind, string ctx)
{
    var n = events.Count(e => e.Kind == kind);
    if (n != 0) throw new InvalidOperationException($"expected 0 {kind} events {ctx}, got {n}");
}

static async Task<List<EventDto>> GetSessionEventsAsync(HttpClient http, Guid sessionId)
{
    using var resp = await http.GetAsync($"/sessions/{sessionId}");
    resp.EnsureSuccessStatusCode();
    var doc = await JsonSerializer.DeserializeAsync<JsonDocument>(
        await resp.Content.ReadAsStreamAsync())
        ?? throw new InvalidOperationException("session detail returned null");
    var list = new List<EventDto>();
    foreach (var e in doc.RootElement.GetProperty("recentEvents").EnumerateArray())
    {
        // EventKind is serialized as an int by default (System.Text.Json
        // numeric enum). Map back to the symbolic name we use in assertions.
        var kindElem = e.GetProperty("kind");
        var kind = kindElem.ValueKind == JsonValueKind.Number
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
            : kindElem.GetString() ?? "";
        list.Add(new EventDto(
            kind,
            e.GetProperty("payloadJson").GetString() ?? "",
            e.GetProperty("occurredAt").GetDateTimeOffset()));
    }
    return list;
}

static async Task<int> EndAndExit(HttpClient http, HubConnection conn, Guid sessionId, int code)
{
    try
    {
        await conn.DisposeAsync();
    }
    catch { /* best effort */ }
    try
    {
        var endResp = await http.PostAsync($"/sessions/{sessionId}/end", content: null);
        endResp.EnsureSuccessStatusCode();
    }
    catch (Exception ex)
    {
        Log($"warn: end session failed: {ex.Message}");
    }
    if (code == 0)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("================================");
        Console.WriteLine("  HEARTBEAT VERIFY: PASS");
        Console.WriteLine("================================");
        Console.ResetColor();
    }
    return code;
}

internal sealed record ClassDto(Guid Id, string Name);
internal sealed record SessionDto(Guid Id);
internal sealed record EventDto(string Kind, string PayloadJson, DateTimeOffset OccurredAt);
