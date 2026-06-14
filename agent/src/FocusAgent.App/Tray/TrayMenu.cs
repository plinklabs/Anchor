using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FocusAgent.App.Tray;

/// <summary>
/// Builds the agent's tray context menu (AA4, #176) on the DS ink treatment so it
/// reads as one piece with the rest of the student-facing agent (MainWindow,
/// FocusOverlay, JoinByCode) — the same ink surface, Space Mono microcopy, on-ink
/// text and hairline rules, with the one magenta spark on the primary action
/// (Open Anchor). The brand voice from ANCHOR_BRAND.md §5 (calm, plain) drives the
/// labels: "Open Anchor" / "Join a session…" / "Exit Anchor".
///
/// The menu is built in code (TrayIconHost has no XAML) but pulls the very same
/// DS resources the XAML windows use, looked up off <see cref="Application.Current"/>'s
/// merged dictionaries — so it can never drift from the design system. A plain
/// system <c>MenuFlyout</c> would otherwise inherit the OS dark theme's grey
/// chrome and Segoe type, which is exactly the off-brand surface this issue fixes.
/// </summary>
internal static class TrayMenu
{
    /// <summary>The status row text shown when the menu first opens (signed out).</summary>
    public const string DefaultStatusText = "SIGNED OUT";

    /// <summary>
    /// The handles a caller (real tray or self-test) needs after the menu is built:
    /// the flyout to attach/show, the status item to keep updated, and the join item
    /// to enable/disable as sessions come and go.
    /// </summary>
    internal sealed record Handles(MenuFlyout Flyout, MenuFlyoutItem StatusItem, MenuFlyoutItem JoinItem);

    /// <summary>
    /// Compose the brand-styled tray menu. Commands are wired to the supplied
    /// actions; the status text starts at <see cref="DefaultStatusText"/> and is
    /// refreshed by <see cref="TrayIconHost.UpdateStatus"/>.
    /// </summary>
    public static Handles Build(Action onOpen, Action onJoinByCode, Action onQuit)
    {
        var flyout = new MenuFlyout
        {
            // Re-skin the flyout chrome to the ink surface (ink fill, hairline-on-ink
            // border, the one 6px radius) instead of the OS dark-theme grey popup.
            MenuFlyoutPresenterStyle = BuildPresenterStyle(),
        };

        // Status row: a Space Mono eyebrow in on-ink-muted — the calm "here's the
        // state" line, never an action. Disabled so it reads as a label, not a target.
        var statusItem = new MenuFlyoutItem
        {
            Text = DefaultStatusText,
            IsEnabled = false,
            FontFamily = Mono,
            FontSize = 11,
            CharacterSpacing = 140,
            Foreground = Brush("PlinkOnInkMutedBrush"),
        };
        flyout.Items.Add(statusItem);

        flyout.Items.Add(Separator());

        // Open Anchor — the primary action, carrying the single magenta spark as a
        // leading glyph in the icon column (the tray menu's <5% magenta, §2). Keeps
        // the native item's hover/focus behaviour; only its icon is the spark.
        var openItem = ActionItem("Open Anchor", onOpen);
        openItem.Icon = MagentaSparkIcon();
        flyout.Items.Add(openItem);

        // Join a session by code — neutral on-ink action; disabled mid-session.
        var joinItem = ActionItem("Join a session…", onJoinByCode);
        flyout.Items.Add(joinItem);

        flyout.Items.Add(Separator());

        // #102: the true exit lives here (the main window's button only closes to
        // the tray). "Exit Anchor" — Anchor proper-noun in prose, per §1.
        var exitItem = ActionItem("Exit Anchor", onQuit);
        flyout.Items.Add(exitItem);

        return new Handles(flyout, statusItem, joinItem);
    }

    // A neutral on-ink action item in Hanken body, on the ink presenter.
    private static MenuFlyoutItem ActionItem(string text, Action onClick) => new()
    {
        Text = text,
        Command = new RelayCommand(onClick),
        FontFamily = Body,
        FontSize = 14,
        Foreground = Brush("PlinkOnInkBrush"),
    };

    // The single spark: a small magenta filled square in the item's icon column —
    // the one bright point on the surface, marking the primary action without
    // repainting it (mirrors the magenta primary button on MainWindow). A PathIcon
    // fills its geometry with the Foreground brush, so it renders the same solid
    // magenta on every machine — unlike a font glyph, whose fill depends on the
    // fallback face (■ came out hollow at small sizes).
    private static PathIcon MagentaSparkIcon() => new()
    {
        // A 7×7 square centred in the ~16px icon box; rounded just slightly to
        // echo the DS 6px radius language without reading as a dot.
        Data = new RectangleGeometry { Rect = new Windows.Foundation.Rect(4.5, 4.5, 7, 7) },
        Foreground = Brush("PlinkMagentaBrush"),
    };

    // Separators are the on-ink hairline (never a heavy divider, never a shadow).
    private static MenuFlyoutSeparator Separator() => new()
    {
        Background = Brush("PlinkHairlineOnInkBrush"),
    };

    private static Style BuildPresenterStyle()
    {
        var style = new Style(typeof(MenuFlyoutPresenter));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brush("PlinkSurfaceInkBrush")));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Brush("PlinkHairlineOnInkBrush")));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(6)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6)));
        return style;
    }

    // --- DS resource lookups off the merged app dictionaries -----------------

    private static SolidColorBrush Brush(string key) =>
        (SolidColorBrush)Application.Current.Resources[key];

    private static FontFamily Mono => (FontFamily)Application.Current.Resources["PlinkMonoFontFamily"];
    private static FontFamily Body => (FontFamily)Application.Current.Resources["PlinkBodyFontFamily"];
}
