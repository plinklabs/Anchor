using System.Diagnostics;
using FocusAgent.Core.Diagnostics;
using FocusAgent.Core.Logging;
using FocusAgent.Core.Tamper;
using FocusAgent.WitnessHost;

namespace FocusAgent.WitnessHost.Tests;

/// <summary>
/// End-to-end of the witness link's headline path (#146 part 1): the REAL host
/// exe connecting to the REAL <see cref="NamedPipeWitnessTransport"/>, and the
/// agent observing the drop when the host's stdin closes — which is exactly what
/// Edge does to the host when the extension is disabled or removed. The only
/// piece this can't drive is the browser itself; the pipe-drop mechanism it
/// reports on is reproduced faithfully here.
/// </summary>
public class WitnessHostIntegrationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>Where the hosts of the specs that don't look at the host's log write it.</summary>
    private static readonly string ScratchLogs =
        Path.Combine(Path.GetTempPath(), "anchor-witness-host-test-logs");

    [Fact]
    public async Task Host_logs_who_started_it_each_connect_attempt_and_why_it_exited()
    {
        // #561: a host Edge started but that couldn't reach the agent's pipe left
        // nothing behind. Its log is what the agent's log upload now carries.
        var pipeName = "anchor-witness-test-" + Guid.NewGuid().ToString("N");
        var logs = Directory.CreateTempSubdirectory("anchor-witness-host-log-").FullName;
        // As Edge starts a host: the calling extension's origin, then the window.
        var host = StartHostProcess(pipeName, logDirectory: logs,
            arguments: ["chrome-extension://dnkimhodjfogjibnbbfdjdapgmmiojio/", "--parent-window=0"]);
        var transport = new NamedPipeWitnessTransport(pipeName: pipeName);
        try
        {
            // No agent yet: the first attempts fail, and say so.
            var log = await WaitForLogAsync(logs, "(attempt 2)");
            Assert.Contains($"started by chrome-extension://dnkimhodjfogjibnbbfdjdapgmmiojio/; agent pipe {pipeName}.", log);
            Assert.Contains($"Connecting to the agent's pipe {pipeName} failed (attempt 1): no answer within 1s (no agent listening on it, or none free)", log);
            Assert.DoesNotContain("--parent-window", log);

            // The agent comes up: the host connects.
            var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            transport.WitnessConnected += (_, _) => connected.TrySetResult();
            await transport.StartAsync();
            await WaitOrFail(connected.Task, "host never connected to the witness pipe");
            await WaitForLogAsync(logs, $"Connected to the agent's pipe {pipeName} (attempt");

            // The agent goes away: the drop is logged.
            await transport.StopAsync();
            await WaitForLogAsync(logs, "The agent's pipe dropped:");

            // Edge closes the port: the host says why it exits.
            host.StandardInput.Close();
            Assert.True(host.WaitForExit(5000), "host process did not exit after stdin closed");
            log = await WaitForLogAsync(logs, "Exiting: Edge closed the extension's port (stdin ended).");

            // In the agent log's format, so the log upload cuts it to the session
            // window; every line names the host's process.
            var lines = log.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.All(lines, line =>
            {
                Assert.True(AgentLogCutter.TryParseAgentTimestamp(line, out _), $"not an agent log line: {line}");
                Assert.Contains($"WitnessHost[{host.Id}]: ", line);
            });
            Assert.Single(Directory.GetFiles(logs), path => Path.GetFileName(path) == $"witness-host-{DateTime.Now:yyyyMMdd}.log");
        }
        finally
        {
            await transport.StopAsync();
            if (!host.HasExited)
            {
                host.Kill(entireProcessTree: true);
                host.WaitForExit(2000);
            }
            host.Dispose();
            try { Directory.Delete(logs, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>The host's log once it holds <paramref name="text"/>.</summary>
    private static async Task<string> WaitForLogAsync(string folder, string text)
    {
        var deadline = DateTime.UtcNow + Timeout;
        var log = "";
        while (DateTime.UtcNow < deadline)
        {
            log = string.Concat(Directory.GetFiles(folder, "witness-host-*.log").Select(ReadShared));
            if (log.Contains(text, StringComparison.Ordinal)) return log;
            await Task.Delay(100);
        }
        Assert.Fail($"The host's log never said \"{text}\". It holds:{Environment.NewLine}{log}");
        return log;
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task Host_connects_then_dropping_its_stdin_signals_a_witness_disconnect()
    {
        // A hermetic pipe name so this never collides with a real agent or a
        // parallel test (the host honours ANCHOR_WITNESS_PIPE).
        var pipeName = "anchor-witness-test-" + Guid.NewGuid().ToString("N");

        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var transport = new NamedPipeWitnessTransport(pipeName: pipeName);
        transport.WitnessConnected += (_, _) => connected.TrySetResult();
        transport.WitnessDisconnected += (_, _) => disconnected.TrySetResult();
        await transport.StartAsync();

        var host = StartHostProcess(pipeName);
        try
        {
            await WaitOrFail(connected.Task, "host never connected to the witness pipe");

            // Simulate the browser tearing the host down: close its stdin. The
            // host's read loop hits EOF, the process exits, the pipe drops.
            host.StandardInput.Close();

            await WaitOrFail(disconnected.Task, "agent never observed the witness drop");
            Assert.True(host.WaitForExit(5000), "host process did not exit after stdin closed");
        }
        finally
        {
            await transport.StopAsync();
            if (!host.HasExited)
            {
                host.Kill(entireProcessTree: true);
                host.WaitForExit(2000);
            }
            host.Dispose();
        }
    }

    [Fact]
    public async Task Host_hands_the_extension_only_the_agent_link_and_what_the_agent_sends()
    {
        // #495: the host used to hand every extension a backend_url and an
        // auth_config as soon as it started, for extensions from before #489 that
        // signed in and connected to the backend themselves. Every extension now gets
        // its session from the agent, so the host writes only its own agent_available /
        // agent_unavailable and the agent's messages, even on a box that still has the
        // old deployment config set.
        var pipeName = "anchor-witness-test-" + Guid.NewGuid().ToString("N");

        var transport = new NamedPipeWitnessTransport(pipeName: pipeName);
        transport.WitnessConnected += (_, connection) =>
            transport.TrySend(connection, "{\"type\":\"welcome\"}");
        await transport.StartAsync();

        var host = StartHostProcess(pipeName, withLegacyConfig: true);
        try
        {
            var types = new List<string?>();
            var deadline = DateTime.UtcNow + Timeout;
            do
            {
                var remaining = deadline - DateTime.UtcNow;
                Assert.True(remaining > TimeSpan.Zero, $"the host never relayed the agent's message; it wrote [{string.Join(", ", types)}]");
                var json = await ReadFramedMessageAsync(host.StandardOutput.BaseStream, remaining);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                types.Add(doc.RootElement.GetProperty("type").GetString());
            }
            while (types[^1] != "welcome");

            Assert.Equal(new[] { "agent_available", "welcome" }, types);
        }
        finally
        {
            await transport.StopAsync();
            host.StandardInput.Close();
            if (!host.WaitForExit(5000))
            {
                host.Kill(entireProcessTree: true);
                host.WaitForExit(2000);
            }
            host.Dispose();
        }
    }

    [Fact]
    public async Task Two_hosts_are_served_at_once_each_on_its_own_connection()
    {
        // #517: every Edge profile runs its own copy of the extension, and each
        // launches its own host. The pipe used to serve one host at a time, so a
        // second profile's host retried forever and its extension never heard of
        // the session.
        var pipeName = "anchor-witness-test-" + Guid.NewGuid().ToString("N");

        var connections = System.Threading.Channels.Channel.CreateUnbounded<WitnessConnection>();
        var drops = System.Threading.Channels.Channel.CreateUnbounded<WitnessConnection>();
        var transport = new NamedPipeWitnessTransport(pipeName: pipeName);
        transport.WitnessConnected += (_, connection) =>
        {
            // What the session relay does on a connect: hand this one extension the state.
            transport.TrySend(connection, $"{{\"type\":\"welcome\",\"connection\":{connection.Id}}}");
            connections.Writer.TryWrite(connection);
        };
        transport.WitnessDisconnected += (_, connection) => drops.Writer.TryWrite(connection);
        await transport.StartAsync();

        var firstHost = StartHostProcess(pipeName);
        var secondHost = StartHostProcess(pipeName);
        try
        {
            using var cts = new CancellationTokenSource(Timeout);
            var a = await connections.Reader.ReadAsync(cts.Token);
            var b = await connections.Reader.ReadAsync(cts.Token);
            Assert.NotEqual(a.Id, b.Id);

            // Each host got the message addressed to its own connection, and only that one.
            var firstWelcome = await ReadUntilAsync(firstHost, "welcome");
            var secondWelcome = await ReadUntilAsync(secondHost, "welcome");
            var firstId = firstWelcome.GetProperty("connection").GetInt64();
            var secondId = secondWelcome.GetProperty("connection").GetInt64();
            Assert.Equal(new[] { a.Id, b.Id }.Order(), new[] { firstId, secondId }.Order());

            // What goes to everyone (the session, its allowlist, its end) reaches both.
            Assert.Equal(2, transport.Broadcast("{\"type\":\"session_started\",\"n\":1}"));
            Assert.Equal(1, (await ReadUntilAsync(firstHost, "session_started")).GetProperty("n").GetInt32());
            Assert.Equal(1, (await ReadUntilAsync(secondHost, "session_started")).GetProperty("n").GetInt32());

            // One profile closes: only its connection drops, the other is still served.
            firstHost.StandardInput.Close();
            var dropped = await drops.Reader.ReadAsync(cts.Token);
            Assert.Equal(firstId, dropped.Id);
            Assert.False(drops.Reader.TryRead(out _));

            Assert.Equal(1, transport.Broadcast("{\"type\":\"session_ended\"}"));
            await ReadUntilAsync(secondHost, "session_ended");
            Assert.False(transport.TrySend(dropped, "{\"type\":\"late\"}"));
        }
        finally
        {
            await transport.StopAsync();
            foreach (var host in new[] { firstHost, secondHost })
            {
                try { host.StandardInput.Close(); } catch { }
                if (!host.WaitForExit(5000))
                {
                    host.Kill(entireProcessTree: true);
                    host.WaitForExit(2000);
                }
                host.Dispose();
            }
        }
    }

    /// <summary>Reads the host's native messages until one of the given type arrives.</summary>
    private static async Task<System.Text.Json.JsonElement> ReadUntilAsync(Process host, string type)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;
            Assert.True(remaining > TimeSpan.Zero, $"the host never wrote a '{type}' message");
            var json = await ReadFramedMessageAsync(host.StandardOutput.BaseStream, remaining);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.GetProperty("type").GetString() == type)
                return doc.RootElement.Clone();
        }
    }

    /// <summary>Reads one 4-byte-length-prefixed UTF-8 native message, bounded by a timeout.</summary>
    private static async Task<string> ReadFramedMessageAsync(Stream stream, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cts.Token);
        var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header);
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload.AsMemory(0, (int)length), cts.Token);
        return System.Text.Encoding.UTF8.GetString(payload);
    }

    private static Process StartHostProcess(
        string pipeName, bool withLegacyConfig = false, string? logDirectory = null, params string[] arguments)
    {
        var hostExe = ResolveHostExe();
        var psi = new ProcessStartInfo(hostExe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);
        psi.Environment["ANCHOR_WITNESS_PIPE"] = pipeName;
        // #561: never into the log folder of an agent installed on this box, whose
        // log upload would carry these lines.
        psi.Environment[AgentLogPaths.WitnessHostLogDirectoryEnvVar] = logDirectory ?? ScratchLogs;
        // What a deployment set for the host before #495, when it still handed the
        // extension a backend URL and Entra config. The host must ignore it now.
        if (withLegacyConfig)
        {
            psi.Environment["ANCHOR_WITNESS_BACKEND_URL"] = "https://backend.test.example";
            psi.Environment["ANCHOR_WITNESS_AUTH_TENANT_ID"] = "tenant-1";
            psi.Environment["ANCHOR_WITNESS_AUTH_CLIENT_ID"] = "client-1";
            psi.Environment["ANCHOR_WITNESS_AUTH_SCOPE"] = "api://x/.default";
        }
        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start the witness host process.");
        return process;
    }

    /// <summary>
    /// The host's apphost exe in its OWN output dir, where its runtimeconfig.json
    /// lives (a project-reference copy beside the test binary has no runtimeconfig).
    /// Mirrors the test's bin\&lt;Config&gt;\&lt;tfm&gt; path into the host project's.
    /// </summary>
    private static string ResolveHostExe()
    {
        var testBase = AppContext.BaseDirectory; // …\tests\FocusAgent.WitnessHost.Tests\bin\<cfg>\<tfm>\
        var hostBase = testBase.Replace(
            Path.Combine("tests", "FocusAgent.WitnessHost.Tests"),
            Path.Combine("src", "FocusAgent.WitnessHost"));
        var exe = Path.Combine(hostBase, "anchor-witness-host.exe");
        if (!File.Exists(exe))
            throw new FileNotFoundException($"Witness host exe not found at {exe}. Build the host project.", exe);
        return exe;
    }

    private static async Task WaitOrFail(Task task, string message)
    {
        var completed = await Task.WhenAny(task, Task.Delay(Timeout));
        Assert.True(completed == task, message);
        await task; // observe any exception
    }
}
