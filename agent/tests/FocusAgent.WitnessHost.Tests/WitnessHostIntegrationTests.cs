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

    private static Process StartHostProcess(string pipeName)
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
