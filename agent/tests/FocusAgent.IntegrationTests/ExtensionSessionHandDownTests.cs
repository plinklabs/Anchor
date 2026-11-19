using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// Asserting e2e for #489: the agent is the extension's only identity. The real
/// agent, signed in as the student against the real backend, hands the session it
/// joined down the witness link — through the real native-messaging host — and
/// reports what the extension sends back with its own connection. Nothing in the
/// browser signs in, so a wrong account, a personal one or a closed sign-in window
/// can no longer leave Edge unfiltered.
///
/// The host runs exactly as Edge runs it: a child process speaking native
/// messaging on its stdin/stdout, pointed at a per-run pipe (<c>ANCHOR_WITNESS_PIPE</c>,
/// the agent's <c>Witness__PipeName</c>). This spec stands where the extension
/// stands, on the other end of that stdio; what the extension does with the
/// messages is the extension e2e's part (<c>extension/e2e/specs</c>).
/// </summary>
[Collection(AgentE2ECollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class ExtensionSessionHandDownTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private readonly BackendFixture _backend;
    public ExtensionSessionHandDownTests(BackendFixture backend) => _backend = backend;

    [Fact]
    public async Task TheAgent_HandsTheExtensionItsSession_AndReportsForIt()
    {
        var api = new BackendClient(_backend.Url);
        var stale = await api.GetRejoinableSessionIdsAsync();
        Assert.True(
            stale.Count == 0,
            $"The student is still in running session(s) {string.Join(", ", stale)} that an earlier spec left " +
            "behind; this agent would rejoin them on connect. End every session a spec starts.");

        var pipeName = "anchor-witness-e2e-" + Guid.NewGuid().ToString("N");
        await using var agent = AgentProcess.Launch(
            _backend.Url,
            TestConfig.StudentOid,
            autoJoin: true,
            extraEnvironment: new Dictionary<string, string> { ["Witness__PipeName"] = pipeName });
        await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));

        // The extension is running before the lesson: outside a session the agent
        // says so, so an extension that missed an end while it was away stops.
        var extension = WitnessHostProcess.Start(pipeName);
        try
        {
            await extension.WaitForAsync(m => Type(m) == "no_session", Wait, "the agent to hand down 'no session'");

            var classId = await api.FindClassIdAsync();
            var ms365 = await api.FindBundleIdAsync("Microsoft 365");
            var session = await api.StartSessionAsync(classId, new[] { ms365 });
            var ended = false;
            try
            {
                // 1. The agent joined (auto-confirm), and hands the session down
                //    with the domains the teacher's bundle allows.
                var started = await extension.WaitForAsync(
                    m => Type(m) == "session_started" && SessionOf(m) == session.Id, Wait,
                    "the agent to hand down the session it joined");
                Assert.Contains(Domains(started, "domains"), d => d.Contains("sharepoint.com"));

                // 2. A block in the browser reaches the teacher's session log, by
                //    its host only (#527). An extension the store hasn't updated
                //    yet still sends the whole URL: the teacher gets its host.
                extension.Send(new
                {
                    type = "blocked_url",
                    sessionId = session.Id,
                    url = "https://older.test/search?q=my+search",
                    host = "older.test",
                    tabId = 4,
                    occurredAt = DateTimeOffset.UtcNow,
                });
                await WaitForEventAsync(api, session.Id, "BlockedUrl", "older.test");
                await AssertBlockedPagesHaveNoUrlAsync(api, session.Id, count: 1);
                extension.Send(new
                {
                    type = "blocked_url",
                    sessionId = session.Id,
                    host = "offlist.test",
                    tabId = 3,
                    occurredAt = DateTimeOffset.UtcNow,
                });
                await WaitForEventAsync(api, session.Id, "BlockedUrl", "offlist.test");
                await AssertBlockedPagesHaveNoUrlAsync(api, session.Id, count: 2);

                // 3. An unblock request from the block page is answered, and lands on
                //    the teacher's list; the teacher's approval comes back down.
                extension.Send(new
                {
                    type = "unblock_request",
                    requestId = "e2e-1",
                    sessionId = session.Id,
                    url = "https://docs.test/notes",
                    host = "docs.test",
                });
                var answer = await extension.WaitForAsync(
                    m => Type(m) == "unblock_request_result" && m.GetProperty("requestId").GetString() == "e2e-1", Wait,
                    "the agent to answer the unblock request");
                Assert.True(answer.GetProperty("ok").GetBoolean(), $"The unblock request was refused: {answer}");
                var pending = await WaitForUnblockRequestAsync(api, session.Id, "docs.test");
                await api.UnblockAsync(session.Id, Assert.Single(pending.RequesterIds), "docs.test");
                var amended = await extension.WaitForAsync(
                    m => Type(m) == "allowlist_amended" && SessionOf(m) == session.Id, Wait,
                    "the approved host to be handed down");
                Assert.Contains("docs.test", Domains(amended, "addedDomains"));

                // 3b. The teacher turns the next request down (#523): the denial
                //     comes back down with its host, the request leaves the
                //     teacher's list, and asking again puts it back on it.
                extension.Send(new
                {
                    type = "unblock_request",
                    requestId = "e2e-2",
                    sessionId = session.Id,
                    url = "https://games.test/play",
                    host = "games.test",
                });
                await extension.WaitForAsync(
                    m => Type(m) == "unblock_request_result" && m.GetProperty("requestId").GetString() == "e2e-2", Wait,
                    "the agent to answer the second unblock request");
                var asked = await WaitForUnblockRequestAsync(api, session.Id, "games.test");
                await api.DenyUnblockAsync(session.Id, Assert.Single(asked.RequesterIds), "games.test");
                var denied = await extension.WaitForAsync(
                    m => Type(m) == "unblock_denied" && SessionOf(m) == session.Id, Wait,
                    "the denial to be handed down");
                Assert.Equal("games.test", denied.GetProperty("host").GetString());
                Assert.DoesNotContain(await api.GetUnblockRequestsAsync(session.Id), r => r.Host == "games.test");
                extension.Send(new
                {
                    type = "unblock_request",
                    requestId = "e2e-3",
                    sessionId = session.Id,
                    url = "https://games.test/play",
                    host = "games.test",
                });
                await WaitForUnblockRequestAsync(api, session.Id, "games.test");

                // 4. The teacher drops the bundle: the extension gets the new list,
                //    the student's grant kept.
                await api.UpdateBundlesAsync(session.Id, Array.Empty<Guid>());
                var replaced = await extension.WaitForAsync(
                    m => Type(m) == "allowlist_replaced" && SessionOf(m) == session.Id, Wait,
                    "the new allowlist to be handed down");
                Assert.DoesNotContain(Domains(replaced, "domains"), d => d.Contains("sharepoint.com"));
                Assert.Contains("docs.test", Domains(replaced, "domains"));

                // 5. Edge restarts (or the extension is switched off and on): the new
                //    host gets the session as it stands, without any sign-in.
                await extension.DisposeAsync();
                extension = WitnessHostProcess.Start(pipeName);
                var snapshot = await extension.WaitForAsync(
                    m => Type(m) == "session_started" && SessionOf(m) == session.Id, Wait,
                    "the agent to hand the session to the reconnected extension");
                Assert.Contains("docs.test", Domains(snapshot, "domains"));
                Assert.DoesNotContain(Domains(snapshot, "domains"), d => d.Contains("sharepoint.com"));

                // 6. The teacher ends the session: the extension stops filtering.
                await api.EndSessionAsync(session.Id);
                ended = true;
                await extension.WaitForAsync(
                    m => Type(m) == "session_ended" && SessionOf(m) == session.Id, Wait,
                    "the end of the session to be handed down");
            }
            finally
            {
                if (!ended)
                {
                    try { await api.EndSessionAsync(session.Id); } catch { /* best-effort */ }
                }
            }
        }
        finally
        {
            await extension.DisposeAsync();
        }
    }

    /// <summary>
    /// #517: every Edge profile runs its own copy of the extension, each with its
    /// own host. A second profile opened during the lesson used to get no session
    /// (the agent's pipe served one host at a time) and browsed unfiltered.
    /// </summary>
    [Fact]
    public async Task ASecondEdgeProfile_OpenedMidSession_IsHandedTheSessionToo()
    {
        var api = new BackendClient(_backend.Url);
        var stale = await api.GetRejoinableSessionIdsAsync();
        Assert.True(
            stale.Count == 0,
            $"The student is still in running session(s) {string.Join(", ", stale)} that an earlier spec left " +
            "behind; this agent would rejoin them on connect. End every session a spec starts.");

        var pipeName = "anchor-witness-e2e-" + Guid.NewGuid().ToString("N");
        await using var agent = AgentProcess.Launch(
            _backend.Url,
            TestConfig.StudentOid,
            autoJoin: true,
            extraEnvironment: new Dictionary<string, string> { ["Witness__PipeName"] = pipeName });
        await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));

        var firstProfile = WitnessHostProcess.Start(pipeName);
        WitnessHostProcess? secondProfile = null;
        try
        {
            await firstProfile.WaitForAsync(m => Type(m) == "no_session", Wait, "the agent to hand down 'no session'");

            var classId = await api.FindClassIdAsync();
            var ms365 = await api.FindBundleIdAsync("Microsoft 365");
            var session = await api.StartSessionAsync(classId, new[] { ms365 });
            var ended = false;
            try
            {
                await firstProfile.WaitForAsync(
                    m => Type(m) == "session_started" && SessionOf(m) == session.Id, Wait,
                    "the agent to hand the session to the first profile");

                // 1. The student switches to another Edge profile: its extension
                //    connects next to the first one and gets the session as it stands.
                secondProfile = WitnessHostProcess.Start(pipeName);
                var started = await secondProfile.WaitForAsync(
                    m => Type(m) == "session_started" && SessionOf(m) == session.Id, Wait,
                    "the agent to hand the session to the second profile");
                Assert.Contains(Domains(started, "domains"), d => d.Contains("sharepoint.com"));
                await WaitForGuardAsync(agent, g => g.WitnessConnections == 2, "both profiles to be connected");

                // 2. A block page in the second profile asks for a site: the answer
                //    comes back to that profile, and the teacher's approval reaches both.
                secondProfile.Send(new
                {
                    type = "unblock_request",
                    requestId = "e2e-second",
                    sessionId = session.Id,
                    url = "https://docs.test/notes",
                    host = "docs.test",
                });
                var answer = await secondProfile.WaitForAsync(
                    m => Type(m) == "unblock_request_result" && m.GetProperty("requestId").GetString() == "e2e-second", Wait,
                    "the agent to answer the second profile's unblock request");
                Assert.True(answer.GetProperty("ok").GetBoolean(), $"The unblock request was refused: {answer}");
                var pending = await WaitForUnblockRequestAsync(api, session.Id, "docs.test");
                await api.UnblockAsync(session.Id, Assert.Single(pending.RequesterIds), "docs.test");
                foreach (var profile in new[] { firstProfile, secondProfile })
                {
                    var amended = await profile.WaitForAsync(
                        m => Type(m) == "allowlist_amended" && SessionOf(m) == session.Id, Wait,
                        "the approved host to be handed down to both profiles");
                    Assert.Contains("docs.test", Domains(amended, "addedDomains"));
                }

                // 3. The second profile's last window closes while the first is still
                //    open: Edge is still filtered there, so the teacher is not told
                //    the extension was switched off and nothing is paused.
                await secondProfile.DisposeAsync();
                secondProfile = null;
                await WaitForGuardAsync(agent, g => g.WitnessConnections == 1, "the second profile's link to drop");
                await Task.Delay(TimeSpan.FromSeconds(2));
                var guard = (await agent.TryGetStatusAsync())?.ExtensionGuard;
                Assert.True(guard is { WitnessConnected: true, State: "Present" }, $"The guard left Present: {guard}");
                Assert.DoesNotContain(await api.GetSessionEventsAsync(session.Id), e => e.Kind == "TamperDetected");

                // 4. The end of the session still reaches the profile that is open.
                await api.EndSessionAsync(session.Id);
                ended = true;
                await firstProfile.WaitForAsync(
                    m => Type(m) == "session_ended" && SessionOf(m) == session.Id, Wait,
                    "the end of the session to be handed down");

                // The answer to the second profile's request never went to the first.
                Assert.False(firstProfile.Received(m => Type(m) == "unblock_request_result"));
            }
            finally
            {
                if (!ended)
                {
                    try { await api.EndSessionAsync(session.Id); } catch { /* best-effort */ }
                }
            }
        }
        finally
        {
            if (secondProfile is not null) await secondProfile.DisposeAsync();
            await firstProfile.DisposeAsync();
        }
    }

    private static async Task WaitForGuardAsync(AgentProcess agent, Func<ExtensionGuardSnapshot, bool> predicate, string what)
    {
        var last = await agent.WaitForAsync(s => s.ExtensionGuard is { } g && predicate(g), Wait);
        Assert.True(
            last?.ExtensionGuard is { } guard && predicate(guard),
            $"Timed out after {Wait.TotalSeconds:N0}s waiting for {what} (guard: {last?.ExtensionGuard}).");
    }

    private static string? Type(JsonElement message) =>
        message.TryGetProperty("type", out var type) ? type.GetString() : null;

    private static Guid? SessionOf(JsonElement message) =>
        message.TryGetProperty("sessionId", out var id) && id.TryGetGuid(out var guid) ? guid : null;

    private static List<string> Domains(JsonElement message, string property) =>
        message.GetProperty(property).EnumerateArray()
            .Select(d => d.GetProperty("value").GetString() ?? "")
            .ToList();

    private static async Task WaitForEventAsync(BackendClient api, Guid sessionId, string kind, string payloadContains)
    {
        var deadline = DateTime.UtcNow + Wait;
        List<BackendClient.SessionEvent> events = new();
        while (DateTime.UtcNow < deadline)
        {
            events = await api.GetSessionEventsAsync(sessionId);
            if (events.Any(e => e.Kind == kind && e.PayloadJson.Contains(payloadContains)))
                return;
            await Task.Delay(250);
        }
        Assert.Fail(
            $"No {kind} event mentioning {payloadContains} reached session {sessionId} within {Wait.TotalSeconds:N0}s " +
            $"(events: {string.Join(", ", events.Select(e => e.Kind))}).");
    }

    /// <summary>
    /// The session's stored BlockedUrl events, as the teacher's past-session page
    /// reads them, name no URL: no path, no query string (#527).
    /// </summary>
    private static async Task AssertBlockedPagesHaveNoUrlAsync(BackendClient api, Guid sessionId, int count)
    {
        var blocked = (await api.GetSessionEventsAsync(sessionId)).Where(e => e.Kind == "BlockedUrl").ToList();
        Assert.Equal(count, blocked.Count);
        Assert.All(blocked, e =>
        {
            Assert.DoesNotContain("\"url\"", e.PayloadJson);
            Assert.DoesNotContain("search", e.PayloadJson);
        });
    }

    private static async Task<BackendClient.PendingUnblockRequest> WaitForUnblockRequestAsync(
        BackendClient api, Guid sessionId, string host)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            var pending = (await api.GetUnblockRequestsAsync(sessionId)).FirstOrDefault(r => r.Host == host);
            if (pending is not null) return pending;
            await Task.Delay(250);
        }
        throw new Xunit.Sdk.XunitException($"The unblock request for {host} never reached the teacher's list.");
    }
}

/// <summary>
/// The real native-messaging host (<c>anchor-witness-host.exe</c>), started the way
/// Edge starts it: stdin/stdout carry length-prefixed JSON, and the host connects
/// to the agent's pipe. Records every message the host writes for the extension.
/// </summary>
internal sealed class WitnessHostProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly List<JsonElement> _received = new();
    private readonly Task _reader;

    private WitnessHostProcess(Process process)
    {
        _process = process;
        _reader = Task.Run(ReadLoopAsync);
    }

    public static WitnessHostProcess Start(string pipeName)
    {
        var psi = new ProcessStartInfo(HostExe())
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["ANCHOR_WITNESS_PIPE"] = pipeName;
        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start the witness host.");
        return new WitnessHostProcess(process);
    }

    /// <summary>Send a message to the agent, as the extension posts one on its native port.</summary>
    public void Send(object message)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payload.Length);
        var stdin = _process.StandardInput.BaseStream;
        stdin.Write(header);
        stdin.Write(payload);
        stdin.Flush();
    }

    /// <summary>The first message (since this host started) that matches, or fail naming what was awaited.</summary>
    public async Task<JsonElement> WaitForAsync(Func<JsonElement, bool> predicate, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (_received)
            {
                foreach (var message in _received)
                    if (predicate(message)) return message;
            }
            await Task.Delay(100);
        }
        string seen;
        lock (_received) seen = string.Join("\n", _received.Select(m => m.GetRawText()));
        throw new Xunit.Sdk.XunitException(
            $"Timed out after {timeout.TotalSeconds:N0}s waiting for {what}. The host wrote:\n{seen}");
    }

    /// <summary>Whether the host has written a matching message so far.</summary>
    public bool Received(Func<JsonElement, bool> predicate)
    {
        lock (_received) return _received.Any(predicate);
    }

    private async Task ReadLoopAsync()
    {
        var stdout = _process.StandardOutput.BaseStream;
        var header = new byte[4];
        try
        {
            while (true)
            {
                await stdout.ReadExactlyAsync(header);
                var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(header);
                var payload = new byte[length];
                await stdout.ReadExactlyAsync(payload);
                using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(payload));
                lock (_received) _received.Add(doc.RootElement.Clone());
            }
        }
        catch
        {
            // The host exited (stdin closed) or the stream ended.
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { _process.StandardInput.Close(); } catch { }
        if (!_process.WaitForExit(5000))
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
            _process.WaitForExit(2000);
        }
        try { await _reader.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        _process.Dispose();
    }

    /// <summary>
    /// The host's apphost in its own output folder, where its runtimeconfig lives;
    /// built with the suite (ProjectReference). The configuration follows the
    /// suite's own (bin\&lt;Configuration&gt;\…).
    /// </summary>
    private static string HostExe()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('\\', '/')).Parent!.Name;
        var bin = Path.Combine(TestConfig.RepoRoot, "agent", "src", "FocusAgent.WitnessHost", "bin");
        var candidates = new[]
        {
            Path.Combine(bin, configuration, "net10.0", "anchor-witness-host.exe"),
            Path.Combine(bin, "x64", configuration, "net10.0", "anchor-witness-host.exe"),
        };
        return candidates.Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            ?? throw new FileNotFoundException(
                $"The witness host was not found at {string.Join(" or ", candidates)}; it is built with the " +
                "integration tests (ProjectReference to FocusAgent.WitnessHost).");
    }
}
