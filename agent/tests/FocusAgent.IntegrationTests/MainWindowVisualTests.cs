using System.Drawing;
using System.Runtime.Versioning;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// Visual e2e for the redesigned agent <c>MainWindow</c> (AA1, #173). Drives the
/// agent's <c>--show-test-mainwindow</c> self-test, which renders the real
/// MainWindow against a synthetic "connected, in a focus session, heartbeat
/// fresh" state with no backend, and asserts on the actual on-screen surface via
/// screenshot capture — the layer a widget/unit test structurally can't reach:
/// that the real WinUI composition (DS ink treatment, bundled Fraunces/Hanken/
/// Space Mono fonts, the Ping motif) paints, and that the surface is *live* (the
/// heartbeat ping animates), not a blank or frozen window.
///
/// The window's state wiring (connection → status line, session join/leave →
/// panel, heartbeat freshness → ping) is covered by the App/MainWindow unit
/// paths; this spec covers the rendering of the real ink window those tests stub
/// out — the composition + font + token bugs that only show in a full-app run.
/// </summary>
[Trait("Category", "Visual")]
[Collection(VisualE2ECollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class MainWindowVisualTests
{
    // A rendered ink surface carries plenty of colour variety: the ink
    // background, the on-ink Fraunces title, the magenta primary button, the
    // session card hairline, the mono labels. A blank/failed render is a flat
    // fill (~1 colour). 8 separates the two with a wide margin.
    private const int MinDistinctColors = 8;

    // The window is the DS full-bleed ink surface (#FF1B1B23, a near-black warm
    // ink). Most of its pixels are that ink, so the captured surface must be
    // dominated by dark pixels — a capture of a typical (light) desktop behind a
    // window that never showed would not be. >=60% leaves slack for the title
    // bar and text while still failing hard on a non-ink/background capture.
    private const double MinDarkFraction = 0.60;

    // The magenta spark (the primary RECONNECT button, #FFDB2777) is the one
    // saturated-magenta region on the surface. Requiring a handful of its pixels
    // proves the real DS-tokened composition painted — a blank window or the
    // desktop background carries no such patch. It also can't be faked by the
    // dark-fraction check alone (a black rect would pass that but lack magenta).
    private const int MinMagentaPixels = 20;

    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task MainWindowSelfTest_RendersTheInkSurface()
    {
        await using var agent = AgentSelfTestProcess.Launch(AgentSelfTestProcess.ShowTestMainWindowArg);

        var hwnd = await agent.WaitForWindowAsync(TimeSpan.FromSeconds(15));
        await Task.Delay(SettleTime);
        var rect = WindowCapture.GetWindowScreenRect(hwnd);

        using var shown = WindowCapture.CaptureRect(rect);
        var saved = VisualArtifacts.Save(shown, "mainwindow");

        // Render: the redesigned MainWindow must paint real content (not blank).
        var colors = WindowCapture.DistinctColorCount(shown);
        Assert.True(
            colors >= MinDistinctColors,
            $"MainWindow capture looks blank: only {colors} distinct colours " +
            $"(need >= {MinDistinctColors}) over {shown.Width}x{shown.Height}px. Saved: {saved}.");

        // Ink treatment: the surface must be dominated by the DS ink background,
        // proving we captured the actual ink window — not the (light) desktop
        // behind a window that never showed (AF4 / ANCHOR_BRAND.md §3).
        var (dark, magenta) = SampleSurface(shown);
        Assert.True(
            dark >= MinDarkFraction,
            $"MainWindow capture is not the ink surface: only {dark:P0} of sampled pixels are dark " +
            $"(need >= {MinDarkFraction:P0}); the capture may be the desktop, not the ink window. Saved: {saved}.");

        // Spark: the magenta primary button must be present, proving the real
        // DS-tokened composition (the one spark on the surface) actually painted.
        Assert.True(
            magenta >= MinMagentaPixels,
            $"MainWindow capture has no magenta spark: only {magenta} magenta pixels sampled " +
            $"(need >= {MinMagentaPixels}); the primary button may not have rendered. Saved: {saved}.");
    }

    // Sample the capture on a coarse grid for (fraction of dark/ink pixels,
    // count of saturated-magenta pixels). Dark ≈ the ink background; magenta ≈
    // the DS spark (#DB2777) — a high red, low green, mid blue with R well above
    // G and B, which neither the ink background nor the on-ink text can satisfy.
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
