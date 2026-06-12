using System.Runtime.Versioning;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// Visual-enforcement e2e for the focus overlay (#133, the Phase-2 visual layer
/// deferred from #131). Drives the agent's <c>--show-test-overlay</c> self-test,
/// which renders the real <c>WinUiFocusOverlay</c> against a synthetic allowlist
/// with no backend, and asserts on the actual on-screen surface via screenshot
/// capture — the layer a unit test structurally can't reach: that the real WinUI
/// composition paints (not blank) and that the close path tears the window down.
///
/// The overlay's enforcement <em>wiring</em> (show on blocked foreground, hide on
/// allowed return, re-minimize on every reactivation #92) is covered by
/// <c>FocusSessionControllerTests</c>; this spec covers the rendering + teardown
/// of the real window those unit tests stub out with a RecordingOverlay.
/// </summary>
[Trait("Category", "Visual")]
[Collection(VisualE2ECollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class OverlayVisualTests
{
    // A rendered WinUI surface carries plenty of colour variety (background,
    // title, the blocked-app line, the allowed-app list); a blank/failed render
    // is a flat fill (~1 colour). 8 separates the two with wide margin.
    private const int MinDistinctColors = 8;

    // The overlay paints a SOLID, opaque background, so once it's gone the same
    // patch of screen changes drastically. Requiring >=10% of pixels to change
    // still fails hard if the "shown" capture was actually the background
    // (0% change), while leaving slack for whatever sits behind it.
    private const double MinShownVsClearedFraction = 0.10;

    // The overlay is shown fullscreen to cover its whole monitor (#103); the
    // FullScreen presenter should land pixel-exact on the monitor bounds, but
    // allow a couple of px of slack for any rounding GetWindowRect may report.
    private const int MonitorCoverageTolerancePx = 2;

    // WinUI needs a beat after the HWND appears to paint and raise the window to
    // its HWND_TOPMOST slot; capturing sooner grabs whatever is still behind it.
    // Matches the dwell the verify-overlay.ps1 script uses.
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task OverlaySelfTest_RendersAVisibleSurface_AndTearsItDownCleanly()
    {
        await using var agent = AgentSelfTestProcess.Launch(AgentSelfTestProcess.ShowTestOverlayArg);

        // SHOW: the real overlay window must appear, then settle so it's painted
        // and topmost before we read pixels.
        var hwnd = await agent.WaitForWindowAsync(TimeSpan.FromSeconds(15));
        await Task.Delay(SettleTime);
        var rect = WindowCapture.GetWindowScreenRect(hwnd);

        // #103: the overlay must cover its ENTIRE monitor — otherwise a student
        // can snap another window into the leftover space or click a taskbar
        // entry beside it. Assert the window rect matches the monitor's full
        // bounds (incl. taskbar) on every edge within a small tolerance. This
        // fails on the pre-#103 480x320 centred window and passes once the
        // overlay is shown fullscreen.
        var monitor = WindowCapture.GetMonitorRectForWindow(hwnd);
        Assert.True(
            Math.Abs(rect.Left - monitor.Left) <= MonitorCoverageTolerancePx &&
            Math.Abs(rect.Top - monitor.Top) <= MonitorCoverageTolerancePx &&
            Math.Abs(rect.Width - monitor.Width) <= MonitorCoverageTolerancePx &&
            Math.Abs(rect.Height - monitor.Height) <= MonitorCoverageTolerancePx,
            $"Overlay does not cover its monitor: window is {rect.Width}x{rect.Height} at " +
            $"({rect.Left},{rect.Top}) but the monitor is {monitor.Width}x{monitor.Height} at " +
            $"({monitor.Left},{monitor.Top}). Expected full-monitor coverage (#103).");

        // ...and actually render (not a blank/transparent window).
        using var shown = WindowCapture.CaptureRect(rect);
        var saved = VisualArtifacts.Save(shown, "overlay-shown");
        var colors = WindowCapture.DistinctColorCount(shown);
        Assert.True(
            colors >= MinDistinctColors,
            $"Overlay capture looks blank: only {colors} distinct colours " +
            $"(need >= {MinDistinctColors}) over {shown.Width}x{shown.Height}px. Saved: {saved}.");

        // HIDE/teardown: the self-test Close()s the overlay ~3s before it exits,
        // so the HWND must go invalid while the process is still alive — proving
        // the close path destroyed the window, not just process exit.
        var tornDown = await agent.WaitForWindowTornDownWhileAliveAsync(hwnd, TimeSpan.FromSeconds(12));
        Assert.True(
            tornDown,
            "Overlay window was not torn down while the agent was still running " +
            "(its HWND stayed valid until the process exited).");

        // ...and that patch of screen must now look different. If the "shown"
        // capture had really been the background (overlay never on top), this
        // post-teardown capture would match it and the assertion would fail —
        // so the test can't be fooled by whatever sits behind the overlay.
        using var cleared = WindowCapture.CaptureRect(rect);
        VisualArtifacts.Save(cleared, "overlay-cleared");
        var changed = WindowCapture.FractionDifferent(shown, cleared);
        Assert.True(
            changed >= MinShownVsClearedFraction,
            $"The overlay's screen region barely changed after teardown ({changed:P1} of pixels); " +
            $"the captured surface may have been the background, not the overlay. Saved: {saved}.");
    }
}
