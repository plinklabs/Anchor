using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// Visual e2e for the redesigned agent tray context menu (AA4, #176). Drives the
/// agent's <c>--show-test-traymenu</c> self-test, which builds the real tray menu
/// via the shared <c>TrayMenu</c> factory (the same one <c>TrayIconHost</c> uses)
/// and shows it open over a small ink host window with no backend, then asserts on
/// the actual on-screen surface via screenshot capture — the layer a unit test
/// structurally can't reach: that the real WinUI composition (DS ink treatment,
/// bundled Space Mono status eyebrow + Hanken actions, hairline rules, the one
/// magenta spark on "Open Anchor") paints as the calm ink surface, not the OS
/// dark-theme grey popup the issue replaces.
///
/// A tray <c>MenuFlyout</c> is a popup, not a window, and a headless run can't click
/// the tray to open it — so the self-test (added in this PR) is how the menu is
/// reached end-to-end. The menu's commands/enablement are pure C# covered by the
/// TrayIconHost wiring; this spec covers the brand rendering those tests stub out —
/// the composition + font + token bugs that only show in a full-app run.
/// </summary>
[Trait("Category", "Visual")]
[Collection(VisualE2ECollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class TrayMenuVisualTests
{
    // A rendered ink menu carries real colour variety: the ink surface, the on-ink
    // Hanken actions, the muted Space Mono status eyebrow, the magenta spark, the
    // hairline separators. A blank/failed render is a flat fill (~1 colour). 8
    // separates the two with a wide margin — matches the other visual specs.
    private const int MinDistinctColors = 8;

    // The menu (and its ink host window) is the DS ink surface (#FF1B1B23). Most of
    // the captured rect is that ink, so it must be dominated by dark pixels — a
    // capture of a typical (light) desktop behind a window that never showed would
    // not be. >=60% leaves slack for the menu chrome + on-ink text while still
    // failing hard on a non-ink/background capture. Matches MainWindow/JoinByCode.
    private const double MinDarkFraction = 0.60;

    // The magenta spark is the small filled square marking the primary "Open Anchor"
    // item (TrayMenu.MagentaSparkIcon, designed as a 7x7 DIP square) — the menu's
    // <5% magenta, deliberately a single small mark rather than a whole button. Its
    // size in a capture follows the display scale, so a fixed count on a coarse
    // sampling grid can't hold across machines: at 125% it renders ~9x9 physical px,
    // which a 4px grid hits at most 3x3 = 9 times (#415). So the spec measures the
    // spark at the capture's own scale: the largest solid patch of spark magenta,
    // found pixel by pixel, must cover at least half the designed spark's physical
    // area (7 DIP x scale, squared). Half leaves room for anti-aliased edge pixels (a
    // 7px square at a half-pixel offset still has a 6x6 solid core, 73% of its area),
    // while a missing/recoloured spark (the brand regression this guards) fails, and
    // stray magenta pixels can't add up to a pass: they aren't one patch.
    private const double SparkSideDip = 7;
    private const double MinSparkAreaFraction = 0.5;

    // The spark must also render at its designed size, not just "at least half of
    // it" (#416): the MenuFlyoutItem template fits the icon into a 16x16 Viewbox,
    // which once scaled the PathIcon by 16/11.5 (its geometry ended at 4.5+7) and
    // painted a ~9.7 DIP spark (12px at 125%). Each side of the patch's bounding box
    // must be within 1.5 DIP of 7: a pixel only counts as spark magenta when the
    // square covers well over half of it, so a correct spark's box measures within
    // about one physical pixel of 7 DIP x scale (<= 1 DIP even at 100%), while the
    // 9.7 DIP spark overshoots by 2.7 DIP.
    private const double SparkSideToleranceDip = 1.5;

    // ...and sit centred in its 16x16 icon slot (#416); the oversized spark sat in
    // the slot's bottom-right, 3.2 DIP off centre on both axes. The slot is invisible,
    // so the spec finds it from the label beside it, which the template lays out
    // from the same slot: the label's text starts 28 DIP right of the slot's left
    // edge (MenuFlyoutItemPlaceholderThemeThickness), so the slot's centre is 28 - 8
    // = 20 DIP left of the label's first ink; and the slot and the label are both
    // vertically centred in the item, which with the bundled Hanken face puts the
    // slot's centre on the middle of the label's capital height (cap top to
    // baseline; measured 0.4 DIP off at 125%). 1.5 DIP absorbs the first glyph's
    // side bearing (< 1 DIP for a capital) and a pixel of anti-aliasing per edge,
    // and is still under half the old 3.2 DIP offset.
    private const double LabelInsetFromSlotCentreDip = 28 - 16 / 2.0;
    private const double SparkCentreToleranceDip = 1.5;

    // How far right of the spark to read its label: the whole of a short label like
    // "Open Anchor" / "Anchor openen" (~95 DIP), well inside the menu, so neither a
    // long label nor the host window's edge can reach the measurement.
    private const double LabelSearchWidthDip = 120;

    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task TrayMenuSelfTest_RendersTheInkMenu()
    {
        await using var agent = AgentSelfTestProcess.Launch(AgentSelfTestProcess.ShowTestTrayMenuArg);

        var hwnd = await agent.WaitForWindowAsync(TimeSpan.FromSeconds(15));
        // The flyout opens ~400ms after the host window appears (and re-shows on a
        // timer); the settle covers the first open landing over the captured rect.
        await Task.Delay(SettleTime);
        var rect = WindowCapture.GetWindowScreenRect(hwnd);
        var scale = WindowCapture.GetWindowScale(hwnd);

        using var shown = WindowCapture.CaptureRect(rect);
        var saved = VisualArtifacts.Save(shown, "traymenu");

        // Render: the redesigned menu must paint real content (not blank).
        var colors = WindowCapture.DistinctColorCount(shown);
        Assert.True(
            colors >= MinDistinctColors,
            $"Tray menu capture looks blank: only {colors} distinct colours " +
            $"(need >= {MinDistinctColors}) over {shown.Width}x{shown.Height}px. Saved: {saved}.");

        // Ink treatment: the surface must be dominated by the DS ink background,
        // proving we captured the actual ink menu — not the (light) desktop behind a
        // window that never showed (AF4 / ANCHOR_BRAND.md §3) and not the OS
        // dark-theme grey popup this issue replaces.
        var dark = SampleDarkFraction(shown);
        Assert.True(
            dark >= MinDarkFraction,
            $"Tray menu capture is not the ink surface: only {dark:P0} of sampled pixels are dark " +
            $"(need >= {MinDarkFraction:P0}); the capture may be the desktop or a grey OS popup. Saved: {saved}.");

        // Spark: the one magenta mark on the primary action must be present, proving
        // the real DS-tokened composition (the single spark) actually painted. The
        // threshold is the spark's own area at this capture's scale, so it holds the
        // same at 100%, 125%, 150% and 200%.
        var spark = LargestMagentaPatch(shown);
        var sparkSidePx = SparkSideDip * scale;
        var minSparkPixels = (int)Math.Ceiling(MinSparkAreaFraction * sparkSidePx * sparkSidePx);
        Assert.True(
            spark.Area >= minSparkPixels,
            $"Tray menu capture has no magenta spark: the largest solid magenta patch is {spark.Area}px " +
            $"({spark.Width}x{spark.Height}), need >= {minSparkPixels}px (half the {SparkSideDip}x{SparkSideDip} DIP " +
            $"spark at {scale:P0} scaling); the primary-action spark may not have rendered. Saved: {saved}.");

        // Size (#416): the spark is the designed 7x7 DIP, not the icon Viewbox's
        // scaled-up ~9.7 DIP.
        var widthDip = spark.Width / scale;
        var heightDip = spark.Height / scale;
        Assert.True(
            Math.Abs(widthDip - SparkSideDip) <= SparkSideToleranceDip &&
            Math.Abs(heightDip - SparkSideDip) <= SparkSideToleranceDip,
            $"Tray menu spark is {widthDip:0.0}x{heightDip:0.0} DIP ({spark.Width}x{spark.Height}px at {scale:P0} " +
            $"scaling), not the designed {SparkSideDip}x{SparkSideDip} DIP (+/- {SparkSideToleranceDip}); the icon " +
            $"slot's Viewbox may be rescaling it. Saved: {saved}.");

        // Centring (#416): the spark sits in the middle of its icon slot, located
        // from the label the template lays out beside the same slot.
        var label = MeasureLabel(shown, spark, scale);
        Assert.True(
            label is not null,
            $"Tray menu capture has no on-ink label beside the spark (searched {LabelSearchWidthDip} DIP right of " +
            $"the spark at {spark.Left},{spark.Top} {spark.Width}x{spark.Height}px); the spark may not sit on its " +
            $"item's row. Saved: {saved}.");
        var sparkCentreX = spark.Left + spark.Width / 2.0;
        var sparkCentreY = spark.Top + spark.Height / 2.0;
        var slotCentreX = label.Value.Left - LabelInsetFromSlotCentreDip * scale;
        var slotCentreY = (label.Value.CapTop + label.Value.Baseline) / 2.0;
        var offsetXDip = (sparkCentreX - slotCentreX) / scale;
        var offsetYDip = (sparkCentreY - slotCentreY) / scale;
        Assert.True(
            Math.Abs(offsetXDip) <= SparkCentreToleranceDip && Math.Abs(offsetYDip) <= SparkCentreToleranceDip,
            $"Tray menu spark is off centre in its icon slot by ({offsetXDip:+0.0;-0.0;0.0}, {offsetYDip:+0.0;-0.0;0.0}) " +
            $"DIP (need within +/- {SparkCentreToleranceDip} on both axes): spark centre ({sparkCentreX:0.0}, " +
            $"{sparkCentreY:0.0})px, slot centre ({slotCentreX:0.0}, {slotCentreY:0.0})px from the label's first ink " +
            $"at x {label.Value.Left} and its cap top {label.Value.CapTop} / baseline {label.Value.Baseline}, at " +
            $"{scale:P0} scaling. Saved: {saved}.");
    }

    // On-ink label text (#FAF7F2 and its anti-aliased edges): clearly lighter than
    // the ink surface, the hairline separators and the dimmed status eyebrow's
    // fainter pixels.
    private static bool IsLabelInk(Color c) => c.R > 120 && c.G > 120 && c.B > 120;

    // The label printed beside the spark, read from the capture: the x of its first
    // ink column, the top of its tallest glyph (a capital; every label starts with
    // one) and its baseline. Reads a band of rows starting at the spark's centre row
    // and growing up and down while rows still carry label ink — the items above and
    // below are separated from it by the item padding, so the band is this one
    // label. The baseline is where the dense x-height band ends: rows at least a
    // quarter as inked as the label's busiest row, which leaves out the sparse
    // ascender tips above and descender tails below. Null when the spark's slot has
    // no label row beside it.
    private static (int Left, int CapTop, int Baseline)? MeasureLabel(Bitmap bmp, MagentaPatch spark, double scale)
    {
        var x0 = spark.Left + spark.Width;
        var x1 = Math.Min(bmp.Width, x0 + (int)Math.Round(LabelSearchWidthDip * scale));
        int RowInk(int y)
        {
            var n = 0;
            for (var x = x0; x < x1; x++)
                if (IsLabelInk(bmp.GetPixel(x, y))) n++;
            return n;
        }

        // Start from the inked row nearest the spark's centre, within half the slot
        // (8 DIP): a spark in its slot always has its label's line beside it.
        var centre = spark.Top + spark.Height / 2;
        var reach = (int)Math.Round(8 * scale);
        int? start = null;
        for (var d = 0; d <= reach && start is null; d++)
        {
            if (centre - d >= 0 && RowInk(centre - d) > 0) start = centre - d;
            else if (centre + d < bmp.Height && RowInk(centre + d) > 0) start = centre + d;
        }
        if (start is null) return null;

        int top = start.Value, bottom = start.Value;
        while (top > 0 && RowInk(top - 1) > 0) top--;
        while (bottom < bmp.Height - 1 && RowInk(bottom + 1) > 0) bottom++;

        var ink = new int[bottom - top + 1];
        for (var y = top; y <= bottom; y++) ink[y - top] = RowInk(y);
        var dense = ink.Max() / 4.0;
        var lastDense = bottom;
        while (lastDense > top && ink[lastDense - top] < dense) lastDense--;

        var left = x1;
        for (var y = top; y <= bottom; y++)
        {
            for (var x = x0; x < left; x++)
            {
                if (!IsLabelInk(bmp.GetPixel(x, y))) continue;
                left = x;
                break;
            }
        }
        return (left, top, lastDense + 1);
    }

    // Fraction of dark/ink pixels on a coarse grid — a ratio, so the grid step
    // doesn't depend on the display scale. Same "dark" definition as the
    // MainWindow/JoinByCode visual specs, so every ink surface is held to one notion
    // of the ink background.
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

    // Saturated magenta ≈ the DS spark (#DB2777). Same definition as the
    // MainWindow/JoinByCode visual specs, so every ink surface is held to one notion
    // of "the spark". A pixel blended mostly into the ink (an anti-aliased edge)
    // falls outside it.
    private static bool IsSparkMagenta(Color c) =>
        c.R > 150 && c.G < 110 && c.B is > 60 and < 170 &&
        c.R - c.G > 80 && c.R - c.B > 40;

    // A solid patch of spark magenta: its pixel count and bounding box (physical px,
    // in capture coordinates).
    private readonly record struct MagentaPatch(int Area, int Left, int Top, int Width, int Height);

    // The largest 4-connected patch of spark-magenta pixels in the capture, read at
    // every pixel (a 1px step, so a 7px spark at 100% is measured in full). Returns
    // its pixel count and bounding box: the count proves the spark is solid, the box
    // gives its size and position.
    private static MagentaPatch LargestMagentaPatch(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var magenta = new bool[w * h];
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new int[w];
            for (var y = 0; y < h; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, w);
                for (var x = 0; x < w; x++)
                    magenta[y * w + x] = IsSparkMagenta(Color.FromArgb(row[x]));
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        var best = new MagentaPatch(0, 0, 0, 0, 0);
        var pending = new Stack<int>();
        for (var start = 0; start < magenta.Length; start++)
        {
            if (!magenta[start]) continue;
            magenta[start] = false;
            pending.Push(start);
            int area = 0, minX = w, maxX = -1, minY = h, maxY = -1;
            while (pending.Count > 0)
            {
                var i = pending.Pop();
                int x = i % w, y = i / w;
                area++;
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
                if (x > 0 && magenta[i - 1]) { magenta[i - 1] = false; pending.Push(i - 1); }
                if (x < w - 1 && magenta[i + 1]) { magenta[i + 1] = false; pending.Push(i + 1); }
                if (y > 0 && magenta[i - w]) { magenta[i - w] = false; pending.Push(i - w); }
                if (y < h - 1 && magenta[i + w]) { magenta[i + w] = false; pending.Push(i + w); }
            }
            if (area > best.Area) best = new MagentaPatch(area, minX, minY, maxX - minX + 1, maxY - minY + 1);
        }
        return best;
    }
}
