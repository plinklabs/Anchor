using System.Collections.Generic;
using FocusAgent.App.Localization;
using FocusAgent.Core.Focus;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FocusAgent.App.Focus;

public sealed partial class FocusOverlayWindow : Window
{
    private readonly IAppIdentifier _launcher;
    private readonly Action<AllowedAppRule> _onLaunched;
    private BrowserPausedPrompt? _browserPrompt;

    public FocusOverlayWindow(IAppIdentifier launcher, Action<AllowedAppRule> onLaunched)
    {
        InitializeComponent();
        _launcher = launcher;
        _onLaunched = onLaunched;
        Title = Loc.Get("Title_FocusSession");
        HeadlineText.Text = Loc.Get("OverlayHeadline");
        MessageText.Text = Loc.Get("OverlayMessage");
    }

    /// <summary>
    /// The paused-Edge copy this window resolved (#427) — read by the
    /// <c>--verify-i18n</c> mode to prove the variant's strings resolve under the
    /// active language. Not part of the runtime flow.
    /// </summary>
    internal (string Headline, string Message, string Action, string Hint) BrowserPausedProbe() =>
        (HeadlineText.Text, MessageText.Text, (string)BrowserRepairButton.Content, BrowserRepairHintText.Text);

    public void UpdateContent(IReadOnlyList<AllowedAppRule> allowedRules, string? blockedAppName)
    {
        // The window is reused across shows, so put back the ordinary copy a
        // paused-Edge show (#427) may have replaced.
        _browserPrompt = null;
        HeadlineText.Text = Loc.Get("OverlayHeadline");
        MessageText.Text = Loc.Get("OverlayMessage");
        BrowserRepairPanel.Visibility = Visibility.Collapsed;

        if (string.IsNullOrWhiteSpace(blockedAppName))
        {
            BlockedText.Visibility = Visibility.Collapsed;
        }
        else
        {
            // Calm, factual microcopy — say what stepped aside, not "you broke a
            // rule" (ANCHOR_BRAND.md §5). Sentence case; the mono style carries
            // the wide tracking.
            BlockedText.Text = Loc.Format("Overlay_BlockedApp", blockedAppName);
            BlockedText.Visibility = Visibility.Visible;
        }

        FillAllowedApps(allowedRules);
    }

    /// <summary>
    /// The overlay for an Edge paused because its extension is off (#427): why
    /// Edge went away, the one action that opens it to switch the extension back
    /// on, and the other allowed apps to carry on with.
    /// </summary>
    public void UpdateBrowserPausedContent(IReadOnlyList<AllowedAppRule> allowedRules, BrowserPausedPrompt prompt)
    {
        _browserPrompt = prompt;
        HeadlineText.Text = Loc.Get("Overlay_BrowserPaused_Headline");
        MessageText.Text = Loc.Get("Overlay_BrowserPaused_Message");
        BrowserRepairButton.Content = Loc.Get("Overlay_BrowserPaused_TurnBackOn");
        BrowserRepairHintText.Text = Loc.Format(
            "Overlay_BrowserPaused_Hint", (int)Math.Round(prompt.RepairWindow.TotalSeconds));
        BrowserRepairPanel.Visibility = Visibility.Visible;
        BlockedText.Visibility = Visibility.Collapsed;

        FillAllowedApps(allowedRules);
    }

    private void FillAllowedApps(IReadOnlyList<AllowedAppRule> allowedRules)
    {
        AllowedAppsPanel.Children.Clear();
        if (allowedRules.Count == 0)
        {
            AllowedAppsPanel.Children.Add(new TextBlock
            {
                Text = Loc.Get("Overlay_NoApps"),
                Style = (Style)Application.Current.Resources["PlinkBodyLargeTextStyle"],
                Foreground = (Brush)Application.Current.Resources["PlinkOnInkMutedBrush"],
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        foreach (var rule in allowedRules)
        {
            AllowedAppsPanel.Children.Add(BuildAllowedAppRow(rule));
        }
    }

    /// <summary>
    /// A hairline row on the ink surface the student can click to step back into
    /// an allowed app. The DS default Button is tuned for paper (ink-on-transparent),
    /// so it would render invisibly on this ink window — the same trap #173 hit on
    /// the MainWindow. Re-skin it here for on-ink: paper-coloured text, a transparent
    /// fill with an on-ink hairline border, flush-left, full width. Never a shadow,
    /// never a colour flip (ANCHOR_BRAND.md §3).
    /// </summary>
    private Button BuildAllowedAppRow(AllowedAppRule rule)
    {
        var res = Application.Current.Resources;
        var button = new Button
        {
            Content = FormatRule(rule),
            Tag = rule,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Foreground = (Brush)res["PlinkOnInkBrush"],
            Background = (Brush)res["PlinkSurfaceInkBrush"],
            BorderBrush = (Brush)res["PlinkHairlineOnInkBrush"],
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 12, 16, 12),
        };
        button.Click += OnAllowedAppClicked;
        return button;
    }

    private void OnAllowedAppClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AllowedAppRule rule })
            return;
        _launcher.LaunchOrActivate(rule);
        _onLaunched(rule);
    }

    private void OnBrowserRepairClicked(object sender, RoutedEventArgs e)
    {
        if (_browserPrompt is not { } prompt)
            return;
        // Open the repair window first, so the foreground change Edge makes when
        // it comes forward already finds it allowed.
        prompt.TurnBackOn();
        _launcher.LaunchOrActivate(prompt.Browser);
        _onLaunched(prompt.Browser);
    }

    private static string FormatRule(AllowedAppRule rule) => rule.MatchKind switch
    {
        AllowedAppMatchKind.ProcessName => rule.Value,
        AllowedAppMatchKind.ExecutablePath => Path.GetFileNameWithoutExtension(rule.Value),
        AllowedAppMatchKind.Publisher => Loc.Format("Overlay_PublisherApps", rule.Value),
        _ => rule.Value,
    };
}
