using System.Runtime.Versioning;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// Visual-enforcement e2e for the join-confirmation toast (#133). Drives the
/// agent's <c>--show-test-toast</c> self-test, which renders the real toast
/// against a synthetic <c>SessionStarted</c> payload with no backend, and
/// asserts the surface actually paints via screenshot capture. This is the
/// "the join toast renders on SessionStarted" path from the issue — the #41
/// chain whose visual end a unit test can't see.
/// </summary>
[Trait("Category", "Visual")]
[Collection(VisualE2ECollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class ToastVisualTests
{
    private const int MinDistinctColors = 8;

    // The toast shows a live "Ns" countdown that ticks every second, so two
    // frames spaced >1s apart MUST differ where the digit redraws. The topmost
    // toast covers its own rect, so nothing behind bleeds through: a toast that
    // never rendered would leave a static region (exactly 0% change, lossless
    // capture). One redrawn digit in a large font is ~0.3% of the window, so a
    // 0.1% floor sits well above zero and ~3x under the real change.
    private const double MinFrameToFrameFraction = 0.001;

    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AcrossOneTick = TimeSpan.FromMilliseconds(1300);

    [Fact]
    public async Task JoinToastSelfTest_RendersAVisibleSurface()
    {
        await using var agent = AgentSelfTestProcess.Launch(AgentSelfTestProcess.ShowTestToastArg);

        var hwnd = await agent.WaitForWindowAsync(TimeSpan.FromSeconds(15));
        await Task.Delay(SettleTime);
        var rect = WindowCapture.GetWindowScreenRect(hwnd);

        // Render: the toast must paint real content (not a blank window).
        using var frameA = WindowCapture.CaptureRect(rect);
        var saved = VisualArtifacts.Save(frameA, "toast");
        var colors = WindowCapture.DistinctColorCount(frameA);
        Assert.True(
            colors >= MinDistinctColors,
            $"Toast capture looks blank: only {colors} distinct colours " +
            $"(need >= {MinDistinctColors}) over {frameA.Width}x{frameA.Height}px. Saved: {saved}.");

        // Live + on top: the countdown digit must redraw across a one-second
        // boundary. If we'd only captured the static background, the two frames
        // would match — so this rules out a toast that never actually showed.
        await Task.Delay(AcrossOneTick);
        using var frameB = WindowCapture.CaptureRect(rect);
        VisualArtifacts.Save(frameB, "toast-later");
        var changed = WindowCapture.FractionDifferent(frameA, frameB);
        Assert.True(
            changed >= MinFrameToFrameFraction,
            $"The toast's countdown did not visibly tick ({changed:P2} of pixels changed across " +
            $"{AcrossOneTick.TotalMilliseconds:N0}ms); the captured region may be the background, " +
            $"not the live toast. Saved: {saved}.");
    }
}
