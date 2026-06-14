using System.Diagnostics;
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
    public async Task Host_sends_the_configured_backend_url_to_the_extension_on_startup()
    {
        // #204: the host hands the extension its backend URL over the native
        // messaging channel as soon as it starts, so a single published extension
        // learns its backend from the on-box agent at runtime. The env var is the
        // per-deployment source (what the agent installer / e2e harness sets).
        var pipeName = "anchor-witness-test-" + Guid.NewGuid().ToString("N");
        const string backendUrl = "https://backend.test.example";

        var transport = new NamedPipeWitnessTransport(pipeName: pipeName);
        await transport.StartAsync();

        var host = StartHostProcess(pipeName, backendUrl);
        try
        {
            // Read the first framed native message off the host's stdout — the
            // backend_url message it emits the moment the bridge starts.
            var stdout = host.StandardOutput.BaseStream;
            var json = await ReadFramedMessageAsync(stdout, Timeout);

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            Assert.Equal("backend_url", doc.RootElement.GetProperty("type").GetString());
            Assert.Equal(backendUrl, doc.RootElement.GetProperty("url").GetString());
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

    private static Process StartHostProcess(string pipeName, string? backendUrl = null)
    {
        var hostExe = ResolveHostExe();
        var psi = new ProcessStartInfo(hostExe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["ANCHOR_WITNESS_PIPE"] = pipeName;
        // Pin a deterministic backend URL for the #204 hand-off test; leaving it
        // unset lets the disconnect test run against the dev fallback, which it
        // never reads.
        if (backendUrl is not null)
            psi.Environment[FocusAgent.WitnessHost.BackendUrlConfig.EnvVarName] = backendUrl;
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
