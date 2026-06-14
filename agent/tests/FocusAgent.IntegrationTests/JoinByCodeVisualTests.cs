using System.Drawing;
using System.Runtime.Versioning;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// Visual e2e for the redesigned join-by-code dialog (AA3, #175). Drives the
/// agent's <c>--show-test-joinbycode</c> self-test, which renders the real
/// <c>JoinByCodeWindow</c> against a no-op join client with no backend, and
/// asserts on the actual on-screen surface via screenshot capture — the layer a
/// widget/unit test structurally can't reach: that the real WinUI composition
/// (DS ink treatment, the bundled Space Mono code field, Fraunces headline) paints,
/// is the calm ink surface (dark-dominant, not the light desktop behind a window
/// that never showed), and carries the one magenta spark (the JOIN button).
///
/// The dialog's behaviour (digit-only filtering, JOIN enablement at 6 digits,
/// inline error/retry, rate-limit) is covered by the backend/agent join-by-code
/// paths and the WinUI host glue; this spec covers the rendering of the real ink
/// window those tests stub out — the composition + font + token bugs that only
/// show in a full-app run (the trap #173/#174 documented).
/// </summary>
[Trait("Category", "Visual")]
[Collection(VisualE2ECollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class JoinByCodeVisualTests
{
    // A rendered ink dialog carries plenty of colour variety: the ink background,
    // the on-ink Fraunces headline + Hanken line, the Space Mono code box, the
    // magenta JOIN button, the hairline borders. A blank/failed render is a flat
    // fill (~1 colour). 8 separates the two with a wide margin. Matches the
    // MainWindow/Overlay specs.
    private const int MinDistinctColors = 8;

    // The dialog is the DS ink surface (#FF1B1B23, a near-black warm ink). Most of
    // its pixels are that ink, so the captured surface must be dominated by dark
    // pixels — a capture of a typical (light) desktop behind a window that never
    // showed would not be. >=60% leaves slack for the title bar, the headline, the
    // code box and buttons while still failing hard on a non-ink/background capture.
    private const double MinDarkFraction = 0.60;

    // The magenta spark (the JOIN button, #FFDB2777) is the one saturated-magenta
    // region on the surface. Requiring a handful of its pixels proves the real
    // DS-tokened composition painted — a blank window or the desktop background
    // carries no such patch, and the dark-fraction check alone can't be faked into
    // producing it (a black rect would pass that but lack magenta).
    private const int MinMagentaPixels = 20;

    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task JoinByCodeSelfTest_RendersTheInkSurface()
    {
        await using var agent = AgentSelfTestProcess.Launch(AgentSelfTestProcess.ShowTestJoinByCodeArg);

        var hwnd = await agent.WaitForWindowAsync(TimeSpan.FromSeconds(15));
        await Task.Delay(SettleTime);
        var rect = WindowCapture.GetWindowScreenRect(hwnd);

        using var shown = WindowCapture.CaptureRect(rect);
        var saved = VisualArtifacts.Save(shown, "joinbycode");

        // Render: the redesigned dialog must paint real content (not blank).
        var colors = WindowCapture.DistinctColorCount(shown);
        Assert.True(
            colors >= MinDistinctColors,
            $"JoinByCode capture looks blank: only {colors} distinct colours " +
            $"(need >= {MinDistinctColors}) over {shown.Width}x{shown.Height}px. Saved: {saved}.");

        // Ink treatment: the surface must be dominated by the DS ink background,
        // proving we captured the actual ink dialog — not the (light) desktop
        // behind a window that never showed (AF4 / ANCHOR_BRAND.md §3).
        var (dark, magenta) = SampleSurface(shown);
        Assert.True(
            dark >= MinDarkFraction,
            $"JoinByCode capture is not the ink surface: only {dark:P0} of sampled pixels are dark " +
            $"(need >= {MinDarkFraction:P0}); the capture may be the desktop, not the ink dialog. Saved: {saved}.");

        // Spark: the magenta JOIN button must be present, proving the real
        // DS-tokened composition (the one spark on the surface) actually painted.
        Assert.True(
            magenta >= MinMagentaPixels,
            $"JoinByCode capture has no magenta spark: only {magenta} magenta pixels sampled " +
            $"(need >= {MinMagentaPixels}); the JOIN button may not have rendered. Saved: {saved}.");
    }

    // Sample the capture on a coarse grid for (fraction of dark/ink pixels, count
    // of saturated-magenta pixels). Dark ≈ the ink background; magenta ≈ the DS
    // spark (#DB2777). Same thresholds/definition as MainWindowVisualTests, so both
    // ink windows are held to one notion of "dark" and "the spark".
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
