using System.Diagnostics;
using System.Runtime.Versioning;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// End-to-end proof for #407: a release that crashes at startup can still be fixed
/// by the next one. Launches the <em>real</em> built agent on a normal startup path
/// with <c>--test-crash-at &lt;stage&gt;</c> (one startup stage throws) and
/// <c>--test-update-probe</c> (the update manager is a probe that claims an installed
/// agent with a newer release on the feed, and logs each phase the real
/// <c>AgentUpdateService</c> drives it through: check, download, pending). Then it
/// plays the student: waits for the real crash dialog, closes it at once, and
/// asserts that
/// <list type="bullet">
///   <item>the update check ran: before the crash where startup reached the update
///   service, or on the fatal path where it didn't (the host never came up);</item>
///   <item>the process did not exit under the in-flight download when the dialog
///   closed, but waited until the fixed release was downloaded and pending for the
///   next launch, then exited non-zero.</item>
/// </list>
///
/// <para>
/// The probe holds its download open until the test creates
/// <c>&lt;probe file&gt;.release</c>, so "still alive after the dialog closed" is
/// observed deterministically rather than raced against a timer. The real Velopack
/// check path is covered by <see cref="UpdateCheckTests"/>; the cadence and
/// join-or-run policy by <c>AgentUpdateServiceTests</c>. The next launch handing a
/// downloaded package to Velopack's updater (<c>VelopackApp.Run</c>, auto-apply on)
/// needs an install, so it is driven by <see cref="UpdateApplyTests"/> (#449), not here.
/// </para>
///
/// No backend is needed: every stage crashes before the agent connects. These runs
/// put a real crash dialog (or message box) on the desktop, so they run in their own
/// non-parallel collection rather than alongside the session specs.
/// </summary>
[Collection(StartupCrashCollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class StartupCrashUpdateTests
{
    private const string WinUiWindowClass = "WinUIDesktop";
    private const string MessageBoxClass = "#32770";

    // The crash dialog's (and the fallback box's) fixed title. Matching on it skips
    // other windows a crashed startup may also show: on a box without the Edge
    // force-install policy, startup queues the extension setup window (#405), which
    // then opens on top of the crash dialog.
    private const string CrashSurfaceTitle = "Anchor";
    private const string CheckLine = "check";
    private const string PendingLine = "pending 9.9.9";

    // CrashReporter.FatalExitCode: the crash dialog's explicit exit.
    private const int FatalExitCode = 1;

    [Fact]
    public async Task Crash_creating_the_main_window_still_runs_the_startup_update_check_and_downloads_the_fix_before_exiting()
    {
        // #403's shape: everything up to MainWindow works, then its creation throws.
        await using var run = CrashRun.Launch("window");
        var dialog = await run.WaitForCrashSurfaceAsync(WinUiWindowClass);

        // The update check was started before any window was created, so it ran
        // even though window creation threw.
        AssertBefore(run.ReadProbe(), CheckLine, "crash-at:window");

        await run.CloseAndAssertProcessWaitsForTheUpdateAsync(dialog);
        var exitCode = await run.ReleaseUpdateAndWaitForExitAsync();

        Assert.Equal(FatalExitCode, exitCode);
        Assert.Contains(PendingLine, run.ReadProbe());
    }

    [Fact]
    public async Task Crash_building_the_host_runs_an_update_check_on_the_fatal_path_and_downloads_the_fix_before_exiting()
    {
        // The earliest startup failure: no DI host, so no update service either.
        // The fatal path builds one from the config files and runs the check itself.
        await using var run = CrashRun.Launch("host");
        var dialog = await run.WaitForCrashSurfaceAsync(WinUiWindowClass);

        var lines = run.ReadProbe();
        AssertBefore(lines, "crash-at:host", CheckLine);
        Assert.Single(lines, l => l == CheckLine);

        await run.CloseAndAssertProcessWaitsForTheUpdateAsync(dialog);
        var exitCode = await run.ReleaseUpdateAndWaitForExitAsync();

        Assert.Equal(FatalExitCode, exitCode);
        Assert.Contains(PendingLine, run.ReadProbe());
    }

    [Fact]
    public async Task Background_thread_crash_waits_for_the_update_check_to_download_the_fix_before_the_process_ends()
    {
        // A fatal exception on a background thread: the runtime ends the process as
        // soon as the handler returns, after the blocking fallback message box.
        await using var run = CrashRun.Launch("background");
        var box = await run.WaitForCrashSurfaceAsync(MessageBoxClass);

        AssertBefore(run.ReadProbe(), CheckLine, "crash-at:background");

        await run.CloseAndAssertProcessWaitsForTheUpdateAsync(box);
        var exitCode = await run.ReleaseUpdateAndWaitForExitAsync();

        // The runtime's unhandled-exception exit code, not the dialog's.
        Assert.NotEqual(0, exitCode);
        Assert.Contains(PendingLine, run.ReadProbe());
    }

    private static void AssertBefore(IReadOnlyList<string> lines, string first, string then)
    {
        var a = lines.ToList().IndexOf(first);
        var b = lines.ToList().IndexOf(then);
        Assert.True(
            a >= 0 && b >= 0 && a < b,
            $"Expected '{first}' before '{then}' in the update probe log, got:\n  {string.Join("\n  ", lines)}");
    }

    /// <summary>One crashing agent launch plus its probe file; killed and cleaned up on dispose.</summary>
    private sealed class CrashRun : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly string _probePath;

        private CrashRun(Process process, string probePath)
        {
            _process = process;
            _probePath = probePath;
        }

        public static CrashRun Launch(string stage)
        {
            if (!File.Exists(TestConfig.AgentExe))
                throw new FileNotFoundException(
                    $"Agent exe not found at {TestConfig.AgentExe}. Build it first: " +
                    "dotnet build agent/src/FocusAgent.App/FocusAgent.App.csproj -p:Platform=x64 -c Debug",
                    TestConfig.AgentExe);

            var probePath = Path.Combine(Path.GetTempPath(), $"anchor-update-probe-{Guid.NewGuid():N}.txt");
            var psi = new ProcessStartInfo(TestConfig.AgentExe) { UseShellExecute = false };
            psi.ArgumentList.Add("--test-crash-at");
            psi.ArgumentList.Add(stage);
            psi.ArgumentList.Add("--test-update-probe");
            psi.Environment["ANCHOR_UPDATE_PROBE_PATH"] = probePath;

            var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start the agent process.");
            return new CrashRun(process, probePath);
        }

        /// <summary>The probe's lines so far (it appends while we read).</summary>
        public IReadOnlyList<string> ReadProbe()
        {
            if (!File.Exists(_probePath)) return [];
            using var stream = new FileStream(_probePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd()
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        /// <summary>Poll until the crash dialog (or fallback box) is on screen.</summary>
        public async Task<IntPtr> WaitForCrashSurfaceAsync(string classPrefix)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                var hwnd = WindowCapture.FindVisibleWindow(_process.Id, classPrefix, CrashSurfaceTitle);
                if (hwnd != IntPtr.Zero) return hwnd;

                if (_process.HasExited)
                    throw new InvalidOperationException(
                        $"Agent exited (code {_process.ExitCode}) before showing its crash surface. " +
                        $"Probe log: [{string.Join(", ", ReadProbe())}]");
                await Task.Delay(100);
            }
            throw new TimeoutException(
                $"No '{classPrefix}' crash surface appeared within 60s. Probe log: [{string.Join(", ", ReadProbe())}]. " +
                $"Windows:\n{WindowCapture.DescribeWindows(_process.Id)}");
        }

        /// <summary>
        /// Close the crash surface the way a student would, straight away, while the
        /// probe still holds the download, and assert the process outlives it: it is
        /// waiting for the update instead of exiting under it.
        /// </summary>
        public async Task CloseAndAssertProcessWaitsForTheUpdateAsync(IntPtr hwnd)
        {
            Assert.DoesNotContain(PendingLine, ReadProbe());

            WindowCapture.PostClose(hwnd);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (WindowCapture.IsVisible(hwnd) && DateTime.UtcNow < deadline)
                await Task.Delay(100);
            Assert.False(WindowCapture.IsVisible(hwnd), "The crash surface did not close on WM_CLOSE.");

            await Task.Delay(TimeSpan.FromSeconds(1.5));
            Assert.False(
                _process.HasExited,
                $"The agent exited (code {(_process.HasExited ? _process.ExitCode : 0)}) as soon as its crash " +
                $"surface closed, under the in-flight update. Probe log: [{string.Join(", ", ReadProbe())}]");
        }

        /// <summary>Let the probe's download finish, then wait for the process to end.</summary>
        public async Task<int> ReleaseUpdateAndWaitForExitAsync()
        {
            await File.WriteAllTextAsync(_probePath + ".release", "release");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                await _process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException(
                    $"Agent did not exit within 60s of the update being released. Probe log: [{string.Join(", ", ReadProbe())}]");
            }
            return _process.ExitCode;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
            catch
            {
                // best-effort teardown
            }
            _process.Dispose();
            try { File.Delete(_probePath); } catch { }
            try { File.Delete(_probePath + ".release"); } catch { }
        }
    }
}

/// <summary>
/// The startup-crash specs each put a real crash dialog on the desktop, so they run
/// serially and never alongside the other specs (whose foreground / minimize
/// assertions a popping dialog could disturb).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class StartupCrashCollection
{
    public const string Name = "startup-crash";
}
