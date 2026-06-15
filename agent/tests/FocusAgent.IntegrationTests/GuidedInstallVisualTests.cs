using System.Drawing;
using System.Runtime.Versioning;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// Visual e2e for the #211 guided-install fallback window. Drives the agent's
/// <c>--show-test-guided-install</c> self-test, which shows the real
/// <c>GuidedInstallWindow</c> against a no-op store launcher with no backend, then
/// asserts on the actual on-screen surface via screenshot capture — the layer a
/// unit test structurally can't reach: that the real WinUI composition (DS ink
/// treatment, the Fraunces headline + Hanken body + Space Mono microcopy, and the
/// one magenta "Open store" spark) paints as the calm ink surface.
///
/// This surface is load-bearing: the guided fallback is the <em>primary</em>
/// install path on any box where the HKCU <c>Software\Policies</c> subtree is
/// ACL-restricted and the force-install policy write is denied (observed on a real
/// machine while building this PR). The registry write/remove path is covered by
/// the unit tests + <c>ExtensionSelfRegisterTests</c>; this spec covers the brand
/// rendering of the fallback those tests stub out.
/// </summary>
[Trait("Category", "Visual")]
[Collection(VisualE2ECollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class GuidedInstallVisualTests
{
    // A rendered ink dialog carries real colour variety: the ink surface, the on-ink
    // headline/body, the muted Space Mono microcopy, the magenta spark, the hairline
    // secondary border. A blank/failed render is a flat fill (~1 colour). 8 separates
    // the two with a wide margin — matches the other visual specs.
    private const int MinDistinctColors = 8;

    // The dialog is the DS ink surface (#FF1B1B23). Most of the captured rect is that
    // ink, so it must be dominated by dark pixels — a capture of a (light) desktop
    // behind a window that never showed would not be. >=60% leaves slack for the
    // on-ink text + the magenta button while still failing hard on a non-ink capture.
    private const double MinDarkFraction = 0.60;

    // The magenta spark is the "OPEN STORE" primary button — the dialog's single
    // magenta accent. A full button samples plenty of magenta pixels on the 4px grid;
    // require >=20 (as the window specs do) so a missing/recoloured button fails, with
    // margin for sub-pixel jitter.
    private const int MinMagentaPixels = 20;

    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task GuidedInstallSelfTest_RendersTheInkDialog()
    {
        await using var agent = AgentSelfTestProcess.Launch(AgentSelfTestProcess.ShowTestGuidedInstallArg);

        var hwnd = await agent.WaitForWindowAsync(TimeSpan.FromSeconds(15));
        await Task.Delay(SettleTime);
        var rect = WindowCapture.GetWindowScreenRect(hwnd);

        using var shown = WindowCapture.CaptureRect(rect);
        var saved = VisualArtifacts.Save(shown, "guided-install");

        var colors = WindowCapture.DistinctColorCount(shown);
        Assert.True(
            colors >= MinDistinctColors,
            $"Guided-install capture looks blank: only {colors} distinct colours " +
            $"(need >= {MinDistinctColors}) over {shown.Width}x{shown.Height}px. Saved: {saved}.");

        var (dark, magenta) = SampleSurface(shown);
        Assert.True(
            dark >= MinDarkFraction,
            $"Guided-install capture is not the ink surface: only {dark:P0} of sampled pixels are dark " +
            $"(need >= {MinDarkFraction:P0}); the capture may be the desktop. Saved: {saved}.");

        Assert.True(
            magenta >= MinMagentaPixels,
            $"Guided-install capture has no magenta spark: only {magenta} magenta pixels sampled " +
            $"(need >= {MinMagentaPixels}); the OPEN STORE button may not have rendered. Saved: {saved}.");
    }

    // Same grid sampling + thresholds as the other ink-surface visual specs, so every
    // ink surface is held to one notion of "dark" and "the spark" (#DB2777).
    private static (double DarkFraction, int MagentaCount) SampleSurface(Bitmap bmp, int gridStep = 4)
    {
        int sampled = 0, dark = 0, magenta = 0;
        for (var y = 0; y < bmp.Height; y += gridStep)
        {
            for (var x = 0; x < bmp.Width; x += gridStep)
            {
                var c = bmp.GetPixel(x, y);
                sampled++;
                if (c.R < 70 && c.G < 70 && c.B < 80) dark++;
                if (c.R > 150 && c.G < 110 && c.B is > 60 and < 170 &&
                    c.R - c.G > 80 && c.R - c.B > 40) magenta++;
            }
        }
        return (sampled == 0 ? 0 : (double)dark / sampled, magenta);
    }
}
