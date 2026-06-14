using System.Drawing;
using System.Runtime.Versioning;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// Visual-enforcement e2e for the join-confirmation toast (#133; re-skinned to
/// the DS ink treatment in AA3 / #175). Drives the agent's
/// <c>--show-test-toast</c> self-test, which renders the real toast against a
/// synthetic <c>SessionStarted</c> payload with no backend, and asserts the
/// surface actually paints — that it is live (the countdown ticks) and that it is
/// the calm DS <em>ink</em> surface (#175, dark-dominant) — via screenshot
/// capture. This is the "the join toast renders on SessionStarted" path from the
/// issue — the #41 chain whose visual end a unit test can't see.
/// </summary>
[Trait("Category", "Visual")]
[Collection(VisualE2ECollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class ToastVisualTests
{
    private const int MinDistinctColors = 8;

    // #175: the redesigned toast is the DS ink surface (#FF1B1B23, a near-black
    // warm ink). Most of its pixels are that ink, so the captured surface must be
    // dominated by dark pixels — a capture of a (light) desktop behind a window
    // that never showed would not be. >=60% leaves slack for the title rule, the
    // Fraunces headline, the Hanken line and the countdown while still failing hard
    // on a non-ink/background capture. Matches the MainWindow/Overlay ink checks.
    private const double MinDarkFraction = 0.60;

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

        // #175: the redesigned toast is the calm DS ink surface — the capture must
        // be dominated by the ink background, proving we grabbed the actual ink
        // toast (the composition/font/token layer the unit tests stub out), not the
        // light desktop behind a window that never showed. Fails on the pre-#175
        // system-chrome toast (a light window on a light OS) and passes once the
        // ink treatment paints (ANCHOR_BRAND.md §3, §6).
        var dark = SampleDarkFraction(frameA);
        Assert.True(
            dark >= MinDarkFraction,
            $"Toast capture is not the ink surface: only {dark:P0} of sampled pixels are dark " +
            $"(need >= {MinDarkFraction:P0}); the capture may be the desktop, not the ink toast. Saved: {saved}.");

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

    // Fraction of sampled pixels that are the DS ink background (#FF1B1B23) — the
    // lever proving the capture is the redesigned ink surface (#175). Same
    // thresholds as MainWindowVisualTests/OverlayVisualTests, so every ink window
    // is held to one definition of "dark".
    private static double SampleDarkFraction(Bitmap bmp, int gridStep = 4)
    {
        int sampled = 0, dark = 0;
        for (var y = 0; y < bmp.Height; y += gridStep)
        {
            for (var x = 0; x < bmp.Width; x += gridStep)
            {
                var c = bmp.GetPixel(x, y);
                sampled++;
                if (c.R < 70 && c.G < 70 && c.B < 80) dark++;
            }
        }
        return sampled == 0 ? 0 : (double)dark / sampled;
    }
}
