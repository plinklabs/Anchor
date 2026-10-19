using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// Asserting e2e for #427: while a joined student's extension is switched off,
/// the agent pauses the browser — its windows come down and stay down — until the
/// extension's witness pipe reconnects, and a reload that reconnects within the
/// grace period pauses nothing.
///
/// Drives the real agent against the real backend over the real witness pipe.
/// Two stand-ins keep it hermetic and off the developer's own browser:
/// <list type="bullet">
/// <item>The extension's side of the pipe is a <see cref="NamedPipeClientStream"/>
/// on a per-run pipe name (<c>Witness__PipeName</c>), connecting exactly like the
/// native host's <c>NamedPipeAgentLink</c>; closing it is what Edge does to the
/// host when the extension is switched off (the host half of that is covered by
/// <c>WitnessHostIntegrationTests</c>).</item>
/// <item>Notepad plays the browser (<c>Witness__BrowserProcessName</c>), allowed
/// through the dev "Notepad (dev)" bundle the way Edge is through the baseline, so
/// the run never minimizes the developer's real Edge windows. The pause logic is
/// the same for any process name; that it targets <c>msedge</c> by default is
/// pinned by the unit tests.</item>
/// </list>
/// Observes through window state (<see cref="WindowCapture.IsIconic"/>) and
/// <c>/status.extensionGuard</c>, and only relies on the agent's own
/// <c>ShowWindow</c> calls — not on foreground hook events, which are the flaky
/// part on a headless desktop (see <see cref="ExtensionOffReMinimizeTests"/> for
/// the restore path, in the Visual lane).
/// </summary>
[Collection(AgentE2ECollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class ExtensionOffPausesBrowserTests
{
    private readonly BackendFixture _backend;
    public ExtensionOffPausesBrowserTests(BackendFixture backend) => _backend = backend;

    [Fact]
    public async Task SwitchingTheExtensionOff_PausesTheBrowser_UntilTheWitnessReconnects()
    {
        var api = new BackendClient(_backend.Url);
        await using var run = await BrowserPauseRun.StartAsync(_backend.Url, api);
        var hwnd = run.BrowserWindow;
        var agent = run.Agent;

        // The stand-in browser is allowed, so the session-start sweep left it up.
        Assert.False(WindowCapture.IsIconic(hwnd), "The allowed stand-in browser was minimized at session start.");

        // 1. An extension reload / update / Edge restart: the pipe drops and comes
        //    back within the grace period. Nothing may be paused.
        await run.Extension.DisconnectAsync();
        await run.WaitForGuardAsync(g => !g.WitnessConnected, TimeSpan.FromSeconds(5), "the witness to drop");
        await run.Extension.ConnectAsync();
        await run.WaitForGuardAsync(
            g => g.WitnessConnected && g.State == "Present", TimeSpan.FromSeconds(5), "the witness to reconnect");
        await Task.Delay(BrowserPauseRun.Grace + TimeSpan.FromSeconds(1));
        var afterReload = await agent.TryGetStatusAsync();
        Assert.Equal("Present", afterReload?.ExtensionGuard?.State);
        Assert.False(
            WindowCapture.IsIconic(hwnd),
            "A drop that reconnected within the grace period minimized the browser — a normal reload was punished.");

        // 2. Switched off: once the grace period runs out the browser is paused and
        //    the window already on screen comes down, with no focus change needed.
        await run.Extension.DisconnectAsync();
        await run.WaitForGuardAsync(
            g => g.State == "Paused", BrowserPauseRun.Grace + TimeSpan.FromSeconds(5), "the browser to be paused");
        Assert.True(
            await BrowserPauseRun.WaitForIconicAsync(hwnd, expected: true, TimeSpan.FromSeconds(5)),
            "The browser was paused but its open window was not minimized.");

        // 3. The overlay's "switch the extension back on": the browser opens for the
        //    repair window (so the student can reach edge://extensions)...
        Assert.True(await agent.TurnExtensionBackOnAsync(), "The agent refused to open the paused browser for repair.");
        await run.WaitForGuardAsync(g => g.State == "Repairing", TimeSpan.FromSeconds(3), "the repair window to open");
        WindowCapture.RestoreWindow(hwnd);
        Assert.True(
            await BrowserPauseRun.WaitForIconicAsync(hwnd, expected: false, TimeSpan.FromSeconds(3)),
            "The browser window could not be restored during the repair window.");
        // ...and, when it runs out without the extension, is paused again.
        await run.WaitForGuardAsync(
            g => g.State == "Paused", BrowserPauseRun.Repair + TimeSpan.FromSeconds(5), "the pause to return after repair");
        Assert.True(
            await BrowserPauseRun.WaitForIconicAsync(hwnd, expected: true, TimeSpan.FromSeconds(5)),
            "The repair window ran out but the browser window was not minimized again.");

        // 4. The extension comes back: the pause lifts at once, and the window can
        //    be brought back and stays up.
        await run.Extension.ConnectAsync();
        await run.WaitForGuardAsync(
            g => g.WitnessConnected && g.State == "Present", TimeSpan.FromSeconds(5), "the pause to lift");
        WindowCapture.RestoreWindow(hwnd);
        Assert.True(
            await BrowserPauseRun.WaitForIconicAsync(hwnd, expected: false, TimeSpan.FromSeconds(3)),
            "The browser window could not be restored after the extension reconnected.");
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Assert.False(WindowCapture.IsIconic(hwnd), "The browser was minimized again after the extension reconnected.");
    }
}

/// <summary>
/// The restore path of #427: while the browser is paused, a student bringing its
/// window back (taskbar click) gets it minimized again, on every attempt, until
/// the extension reconnects. Relies on the real foreground hook's MINIMIZEEND
/// events, so it runs in the non-blocking Visual lane like
/// <see cref="ReMinimizeOnRestoreTests"/>.
/// </summary>
[Trait("Category", "Visual")]
[Collection(AgentE2ECollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class ExtensionOffReMinimizeTests
{
    private const int RestoreCycles = 3;

    private readonly BackendFixture _backend;
    public ExtensionOffReMinimizeTests(BackendFixture backend) => _backend = backend;

    [Fact]
    public async Task RestoringThePausedBrowser_GetsItMinimizedAgain_UntilTheExtensionReconnects()
    {
        var api = new BackendClient(_backend.Url);
        await using var run = await BrowserPauseRun.StartAsync(_backend.Url, api);
        var hwnd = run.BrowserWindow;

        await run.Extension.DisconnectAsync();
        await run.WaitForGuardAsync(
            g => g.State == "Paused", BrowserPauseRun.Grace + TimeSpan.FromSeconds(5), "the browser to be paused");
        Assert.True(
            await BrowserPauseRun.WaitForIconicAsync(hwnd, expected: true, TimeSpan.FromSeconds(5)),
            "The browser was paused but its open window was not minimized.");

        for (var i = 0; i < RestoreCycles; i++)
        {
            WindowCapture.RestoreWindow(hwnd);
            Assert.True(
                await BrowserPauseRun.WaitForIconicAsync(hwnd, expected: true, TimeSpan.FromSeconds(5)),
                $"The paused browser stayed restored after restore #{i + 1} — the pause is not enforced on restore.");
        }

        // Control: once the extension is back, a restore sticks — so the
        // re-minimizes above were the pause, not a restore that never happened.
        await run.Extension.ConnectAsync();
        await run.WaitForGuardAsync(g => g.State == "Present", TimeSpan.FromSeconds(5), "the pause to lift");
        WindowCapture.RestoreWindow(hwnd);
        Assert.True(
            await BrowserPauseRun.WaitForIconicAsync(hwnd, expected: false, TimeSpan.FromSeconds(5)),
            "After the extension reconnected, restoring the browser did not un-minimize it.");
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.False(WindowCapture.IsIconic(hwnd), "The browser was minimized again after the extension reconnected.");
    }
}

/// <summary>
/// One #427 scenario: a fresh Notepad standing in for the browser, the agent
/// launched with a private witness pipe and short pause timings, a fake extension
/// connected on that pipe, and a session joined with Notepad allowed.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class BrowserPauseRun : IAsyncDisposable
{
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan Repair = TimeSpan.FromSeconds(8);

    private readonly BackendClient _api;
    private Guid? _sessionId;
    private AgentProcess? _agent;

    private BrowserPauseRun(BackendClient api, FakeExtensionWitness extension, IntPtr window)
    {
        _api = api;
        Extension = extension;
        BrowserWindow = window;
    }

    public AgentProcess Agent => _agent ?? throw new InvalidOperationException("Agent not launched.");
    public FakeExtensionWitness Extension { get; }
    public IntPtr BrowserWindow { get; }

    public static async Task<BrowserPauseRun> StartAsync(string backendUrl, BackendClient api)
    {
        var pipeName = "anchor-witness-e2e-" + Guid.NewGuid().ToString("N");

        // A fresh window must be up before the session starts (Win11 notepad is a
        // tabbed launcher, so kill leftovers first to guarantee a new window).
        KillNotepad();
        using (Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true })
               ?? throw new InvalidOperationException("Failed to launch notepad."))
        {
        }
        var hwnd = await WaitForNotepadWindowAsync(TimeSpan.FromSeconds(10));
        Assert.True(hwnd != IntPtr.Zero, "Notepad did not present a top-level window within 10s.");

        var run = new BrowserPauseRun(api, new FakeExtensionWitness(pipeName), hwnd);
        try
        {
            run._agent = AgentProcess.Launch(
                backendUrl,
                TestConfig.StudentOid,
                autoJoin: true,
                extraEnvironment: new Dictionary<string, string>
                {
                    ["Witness__PipeName"] = pipeName,
                    ["Witness__BrowserProcessName"] = TestConfig.AppProcessName,
                    ["Session__ExtensionGraceSeconds"] = ((int)Grace.TotalSeconds).ToString(),
                    ["Session__ExtensionRepairSeconds"] = ((int)Repair.TotalSeconds).ToString(),
                });
            await run.Agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));

            // The extension is installed and running before the lesson starts.
            await run.Extension.ConnectAsync();
            await run.WaitForGuardAsync(g => g.WitnessConnected, TimeSpan.FromSeconds(10), "the witness to connect");

            var classId = await api.FindClassIdAsync();
            var browserBundle = await api.FindBundleIdAsync(TestConfig.AppBundleName);
            var session = await api.StartSessionAsync(classId, new[] { browserBundle });
            run._sessionId = session.Id;

            var joined = await run.Agent.WaitForAsync(
                s => s.JoinedSessionId == session.Id
                     && (s.AllowedApps ?? Array.Empty<string>()).Contains(TestConfig.AppProcessName)
                     && s.ExtensionGuard?.State == "Present",
                TimeSpan.FromSeconds(10));
            Assert.True(
                joined?.JoinedSessionId == session.Id && joined.ExtensionGuard?.State == "Present",
                $"Agent did not join with the browser allowed and the extension present within 10s " +
                $"(joinedSessionId: {joined?.JoinedSessionId?.ToString() ?? "<none>"}, " +
                $"guard: {joined?.ExtensionGuard?.State ?? "<none>"}).");
            return run;
        }
        catch
        {
            await run.DisposeAsync();
            throw;
        }
    }

    /// <summary>Poll /status until the guard matches, or fail naming what was awaited.</summary>
    public async Task WaitForGuardAsync(Func<ExtensionGuardSnapshot, bool> predicate, TimeSpan timeout, string what)
    {
        var last = await Agent.WaitForAsync(s => s.ExtensionGuard is { } g && predicate(g), timeout);
        Assert.True(
            last?.ExtensionGuard is { } g && predicate(g),
            $"Timed out after {timeout.TotalSeconds:N0}s waiting for {what} " +
            $"(witnessConnected: {last?.ExtensionGuard?.WitnessConnected.ToString() ?? "<none>"}, " +
            $"state: {last?.ExtensionGuard?.State ?? "<none>"}).");
    }

    /// <summary>Poll until <paramref name="hwnd"/>'s iconic state matches, or time out.</summary>
    public static async Task<bool> WaitForIconicAsync(IntPtr hwnd, bool expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (WindowCapture.IsWindow(hwnd) && WindowCapture.IsIconic(hwnd) == expected)
                return true;
            await Task.Delay(100);
        }
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_sessionId is { } id)
        {
            try { await _api.EndSessionAsync(id); } catch { /* best-effort */ }
        }
        await Extension.DisposeAsync();
        if (_agent is not null)
            await _agent.DisposeAsync();
        KillNotepad();
    }

    private static async Task<IntPtr> WaitForNotepadWindowAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var hwnd = WindowCapture.FindMainWindowByProcessName("notepad");
            if (hwnd != IntPtr.Zero) return hwnd;
            await Task.Delay(200);
        }
        return IntPtr.Zero;
    }

    private static void KillNotepad()
    {
        foreach (var p in Process.GetProcessesByName("notepad"))
        {
            using (p)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            }
        }
    }
}

/// <summary>
/// The extension's end of the witness pipe, as the native host holds it: a
/// write-only client on the agent's pipe (<c>NamedPipeAgentLink</c>). Connected
/// means the extension is running; closing it is the extension switched off.
/// </summary>
internal sealed class FakeExtensionWitness : IAsyncDisposable
{
    private readonly string _pipeName;
    private NamedPipeClientStream? _pipe;

    public FakeExtensionWitness(string pipeName) => _pipeName = pipeName;

    public async Task ConnectAsync()
    {
        await DisconnectAsync();
        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        try
        {
            // The agent re-arms its pipe server right after a drop; the connect
            // waits for it.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await pipe.ConnectAsync(cts.Token);
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }
        _pipe = pipe;
    }

    public async Task DisconnectAsync()
    {
        var pipe = _pipe;
        _pipe = null;
        if (pipe is not null)
            await pipe.DisposeAsync();
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();
}
