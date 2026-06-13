using System.Diagnostics;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// Launches the real agent exe in one of its dev-only <em>self-test</em> modes
/// (<c>--show-test-overlay</c> / <c>--show-test-toast</c>) and exposes its
/// process + window state to the visual specs (#133). Unlike
/// <see cref="AgentProcess"/>, a self-test run does no host / WAM / hub /
/// backend bootstrap and exposes no <c>/status</c> — it just renders the one
/// WinUI surface against a synthetic payload and exits — so this launcher
/// observes the surface through its HWND instead of polling JSON.
///
/// Self-test modes skip single-instance gating (see <c>Program.Main</c>), and
/// the window probe matches on this process's PID, so a developer's own running
/// agent never collides with the throwaway self-test process.
/// </summary>
internal sealed class AgentSelfTestProcess : IAsyncDisposable
{
    /// <summary>Dev-only self-test flags (mirror <c>Program</c> in the agent).</summary>
    public const string ShowTestOverlayArg = "--show-test-overlay";
    public const string ShowTestToastArg = "--show-test-toast";

    private readonly Process _process;

    private AgentSelfTestProcess(Process process) => _process = process;

    public int Pid => _process.Id;
    public bool HasExited => _process.HasExited;

    public static AgentSelfTestProcess Launch(string selfTestFlag)
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("The agent exe is Windows-only.");
        if (!File.Exists(TestConfig.AgentExe))
            throw new FileNotFoundException(
                $"Agent exe not found at {TestConfig.AgentExe}. Build it first: " +
                "dotnet build agent/src/FocusAgent.App/FocusAgent.App.csproj -p:Platform=x64 -c Debug",
                TestConfig.AgentExe);

        var psi = new ProcessStartInfo(TestConfig.AgentExe) { UseShellExecute = false };
        psi.ArgumentList.Add(selfTestFlag);

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start the agent self-test process.");
        return new AgentSelfTestProcess(process);
    }

    /// <summary>
    /// Poll until the agent's WinUI surface appears, returning its HWND. Throws
    /// with a window dump if it never shows or the process dies first.
    /// </summary>
    public async Task<IntPtr> WaitForWindowAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var hwnd = WindowCapture.FindAgentWindow(Pid);
            if (hwnd != IntPtr.Zero) return hwnd;

            if (_process.HasExited)
            {
                throw new InvalidOperationException(
                    $"Agent self-test process exited (code {_process.ExitCode}) before any window appeared. " +
                    $"Windows seen:\n{WindowCapture.DescribeWindows(Pid)}");
            }
            await Task.Delay(100);
        }

        throw new TimeoutException(
            $"Agent self-test window did not appear within {timeout.TotalSeconds:N0}s. " +
            $"Windows owned by pid {Pid}:\n{WindowCapture.DescribeWindows(Pid)}");
    }

    /// <summary>
    /// Poll until <paramref name="hwnd"/> goes invalid <em>while this process is
    /// still running</em> — i.e. the window was actually torn down, not merely
    /// destroyed as a side effect of the process exiting. Returns false if the
    /// process exits first (no teardown observed) or the timeout elapses.
    /// Records <see cref="LastTeardownTrace"/> with the outcome so a failing
    /// assertion can say *why* (process exited early vs. HWND never invalidated) —
    /// the distinction that diagnosed the #160 flake.
    /// </summary>
    public async Task<bool> WaitForWindowTornDownWhileAliveAsync(IntPtr hwnd, TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        var deadline = start + timeout;
        var polls = 0;
        while (DateTime.UtcNow < deadline)
        {
            polls++;
            if (_process.HasExited)
            {
                LastTeardownTrace = $"process EXITED after {(DateTime.UtcNow - start).TotalSeconds:N1}s / {polls} polls (code {_process.ExitCode})";
                return false;            // died before we saw teardown
            }
            if (!WindowCapture.IsWindow(hwnd))
            {
                LastTeardownTrace = $"torn down after {(DateTime.UtcNow - start).TotalSeconds:N1}s / {polls} polls";
                return true;  // gone, and (just checked) still alive
            }
            await Task.Delay(150);
        }
        LastTeardownTrace = $"TIMED OUT after {(DateTime.UtcNow - start).TotalSeconds:N1}s / {polls} polls; hwnd still valid, process alive={!_process.HasExited}";
        return false;
    }

    /// <summary>Outcome of the last <see cref="WaitForWindowTornDownWhileAliveAsync"/> call, for failure messages.</summary>
    public string? LastTeardownTrace { get; private set; }

    public void Kill()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch
        {
            // best-effort
        }
    }

    public async ValueTask DisposeAsync()
    {
        Kill();
        try { await _process.WaitForExitAsync(); }
        catch { /* teardown best-effort */ }
        _process.Dispose();
    }
}
