using System.Drawing;
using System.Runtime.Versioning;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// Visual e2e for the #248 last-resort crash dialog. Drives the agent's
/// <c>--show-test-crash</c> self-test, which shows the real
/// <c>CrashReportWindow</c> against a synthetic startup exception with no backend,
/// then asserts on the actual on-screen surface via screenshot capture — the layer
/// a unit test structurally can't reach: that the window the agent throws up when
/// it would otherwise vanish silently actually <em>paints</em> (a real, non-blank
/// surface with its magenta Copy spark) rather than being another invisible exit.
///
/// This window is deliberately self-contained — code-only control tree, explicit
/// brushes, no DS resource lookups — precisely so it can render when a normal
/// window's dependencies have failed; this spec proves that composition paints for
/// real. The curated detail text is covered by <c>CrashDiagnosticsTests</c>.
/// </summary>
[Trait("Category", "Visual")]
[Collection(VisualE2ECollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class CrashDialogVisualTests
{
    // The dialog carries real colour variety: the ink surface, the on-ink headline,
    // the muted intro, the selectable detail field, the expander, the magenta Copy
    // button. A blank/failed render is a flat fill (~1 colour). 8 separates the two
    // with a wide margin — matches the other visual specs.
    private const int MinDistinctColors = 8;

    // The window is the ink surface (#FF1B1B23) with a dark detail field
    // (#FF262630); both count as dark, so the captured rect must be dominated by
    // dark pixels — a capture of a (light) desktop behind a window that never showed
    // would not be. >=60% leaves slack for the on-ink text + the magenta button.
    private const double MinDarkFraction = 0.60;

    // The magenta spark is the "Copy" button — the dialog's single magenta accent.
    // Require >=20 pixels sampled on the 4px grid (as the other specs do) so a
    // missing/recoloured button fails, with margin for sub-pixel jitter.
    private const int MinMagentaPixels = 20;

    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task CrashDialogSelfTest_RendersTheDialog()
    {
        await using var agent = AgentSelfTestProcess.Launch(AgentSelfTestProcess.ShowTestCrashArg);

        var hwnd = await agent.WaitForWindowAsync(TimeSpan.FromSeconds(15));
        await Task.Delay(SettleTime);
        var rect = WindowCapture.GetWindowScreenRect(hwnd);

        using var shown = WindowCapture.CaptureRect(rect);
        var saved = VisualArtifacts.Save(shown, "crash-dialog");

        var colors = WindowCapture.DistinctColorCount(shown);
        Assert.True(
            colors >= MinDistinctColors,
            $"Crash dialog capture looks blank: only {colors} distinct colours " +
            $"(need >= {MinDistinctColors}) over {shown.Width}x{shown.Height}px. Saved: {saved}.");

        var (dark, magenta) = SampleSurface(shown);
        Assert.True(
            dark >= MinDarkFraction,
            $"Crash dialog capture is not the ink surface: only {dark:P0} of sampled pixels are dark " +
            $"(need >= {MinDarkFraction:P0}); the capture may be the desktop. Saved: {saved}.");

        Assert.True(
            magenta >= MinMagentaPixels,
            $"Crash dialog capture has no magenta spark: only {magenta} magenta pixels sampled " +
            $"(need >= {MinMagentaPixels}); the Copy button may not have rendered. Saved: {saved}.");
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
