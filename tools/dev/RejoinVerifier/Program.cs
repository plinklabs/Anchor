using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;

// Headless end-to-end verifier for the post-restart rejoin endpoint (#54).
//
// Asserts the contract /sessions/rejoinable enforces:
//   1. Before the student joins, /sessions/rejoinable is empty.
//   2. After JoinSession, /sessions/rejoinable contains exactly that session.
//   3. After the student calls LeaveSession (LeftAt set), it disappears.
//   4. After re-JoinSession (LeftAt clears, JoinedAt persists), it reappears.
//   5. After the teacher ends the session (EndedAt set), it disappears.
//
// This mirrors what the agent's HttpSessionRehydrationClient + SessionCoordinator
// rely on after a crash-restart, without needing the real WinUI agent.

const string BackendUrlFlag = "--backend";
const string TeacherOidFlag = "--teacher-oid";
const string StudentOidFlag = "--student-oid";
const string ClassNameFlag = "--class-name";

var backendUrl = ArgOr(args, BackendUrlFlag, "http://localhost:5276");
var teacherOid = ArgOr(args, TeacherOidFlag, "11111111-1111-1111-1111-111111111111");
var studentOid = ArgOr(args, StudentOidFlag, "22222222-2222-2222-2222-222222222222");
var className = ArgOr(args, ClassNameFlag, "3A");

using var teacherHttp = new HttpClient { BaseAddress = new Uri(backendUrl), Timeout = TimeSpan.FromSeconds(10) };
teacherHttp.DefaultRequestHeaders.Add("X-Dev-Impersonate-Oid", teacherOid);

using var studentHttp = new HttpClient { BaseAddress = new Uri(backendUrl), Timeout = TimeSpan.FromSeconds(10) };
studentHttp.DefaultRequestHeaders.Add("X-Dev-Impersonate-Oid", studentOid);

Log($"backend={backendUrl} teacher={teacherOid} student={studentOid}");

Log("Resolving class id…");
var classes = await teacherHttp.GetFromJsonAsync<List<ClassDto>>("/classes")
    ?? throw new InvalidOperationException("/classes returned null");
var target = classes.FirstOrDefault(c => c.Name == className)
    ?? throw new InvalidOperationException($"Class '{className}' not found.");

Log($"POST /sessions for class {target.Id}…");
var createResp = await teacherHttp.PostAsJsonAsync("/sessions", new { classId = target.Id, bundleIds = Array.Empty<Guid>() });
createResp.EnsureSuccessStatusCode();
var session = await createResp.Content.ReadFromJsonAsync<SessionDto>()
    ?? throw new InvalidOperationException("/sessions returned null");
Log($"Session created: {session.Id}");

try
{
    // 1. Before JoinSession: rejoinable should NOT contain this session
    // (participant row exists but JoinedAt is null at this point).
    var before = await GetRejoinableAsync(studentHttp);
    if (before.Any(s => s.Id == session.Id))
    {
        Fail($"PRE-JOIN check failed: /sessions/rejoinable already contains {session.Id} before student joined.");
        return 2;
    }
    Log("PASS: pre-join /sessions/rejoinable excludes the session (JoinedAt not set yet).");

    // 2. Open SignalR + JoinSession.
    Log("Opening SignalR connection as Dev Student…");
    await using var conn = new HubConnectionBuilder()
        .WithUrl(new Uri($"{backendUrl}/hubs/session"), opts =>
        {
            opts.Headers["X-Dev-Impersonate-Oid"] = studentOid;
        })
        .Build();
    await conn.StartAsync();
    await conn.InvokeAsync("JoinSession", new { SessionId = session.Id, JoinCode = (string?)null });
    Log("JoinSession sent.");

    var afterJoin = await GetRejoinableAsync(studentHttp);
    if (!afterJoin.Any(s => s.Id == session.Id))
    {
        Fail($"POST-JOIN check failed: /sessions/rejoinable does not contain {session.Id} after JoinSession.");
        return 3;
    }
    Log("PASS: post-join /sessions/rejoinable contains the session.");

    // 3. LeaveSession -> participant.LeftAt set -> not rejoinable.
    await conn.InvokeAsync("LeaveSession", session.Id);
    Log("LeaveSession sent.");
    var afterLeave = await GetRejoinableAsync(studentHttp);
    if (afterLeave.Any(s => s.Id == session.Id))
    {
        Fail($"POST-LEAVE check failed: /sessions/rejoinable still contains {session.Id} after LeaveSession.");
        return 4;
    }
    Log("PASS: post-leave /sessions/rejoinable excludes the session (LeftAt set).");

    // 4. JoinSession again -> LeftAt cleared, JoinedAt still set -> rejoinable
    // again. This matches the actual restart-rejoin path: SessionHub.JoinSession
    // sets JoinedAt ??= now and LeftAt = null.
    await conn.InvokeAsync("JoinSession", new { SessionId = session.Id, JoinCode = (string?)null });
    Log("Second JoinSession sent.");
    var afterRejoin = await GetRejoinableAsync(studentHttp);
    if (!afterRejoin.Any(s => s.Id == session.Id))
    {
        Fail($"POST-REJOIN check failed: /sessions/rejoinable does not contain {session.Id} after second JoinSession.");
        return 5;
    }
    Log("PASS: post-rejoin /sessions/rejoinable contains the session again.");

    // 5. Teacher ends session -> EndedAt set -> not rejoinable for anyone.
    var endResp = await teacherHttp.PostAsync($"/sessions/{session.Id}/end", content: null);
    endResp.EnsureSuccessStatusCode();
    var afterEnd = await GetRejoinableAsync(studentHttp);
    if (afterEnd.Any(s => s.Id == session.Id))
    {
        Fail($"POST-END check failed: /sessions/rejoinable still contains {session.Id} after teacher ended it.");
        return 6;
    }
    Log("PASS: post-end /sessions/rejoinable excludes the ended session.");

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("================================");
    Console.WriteLine("  REJOIN VERIFY: PASS");
    Console.WriteLine("================================");
    Console.ResetColor();
    return 0;
}
catch (Exception ex)
{
    Fail(ex.Message);
    // Best-effort: end the session so it doesn't pollute the dev DB.
    try { await teacherHttp.PostAsync($"/sessions/{session.Id}/end", content: null); } catch { /* swallow */ }
    return 1;
}

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

static async Task<List<SessionDto>> GetRejoinableAsync(HttpClient http)
{
    using var resp = await http.GetAsync("/sessions/rejoinable");
    resp.EnsureSuccessStatusCode();
    using var stream = await resp.Content.ReadAsStreamAsync();
    using var doc = await JsonDocument.ParseAsync(stream);
    var list = new List<SessionDto>();
    if (doc.RootElement.ValueKind == JsonValueKind.Array)
    {
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            list.Add(new SessionDto(el.GetProperty("id").GetGuid()));
        }
    }
    return list;
}

internal sealed record ClassDto(Guid Id, string Name);
internal sealed record SessionDto(Guid Id);
