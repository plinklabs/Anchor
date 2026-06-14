using System.Collections.Generic;
using FocusAgent.Core.Focus;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FocusAgent.App.Focus;

public sealed partial class FocusOverlayWindow : Window
{
    private readonly IAppIdentifier _launcher;
    private readonly Action<AllowedAppRule> _onLaunched;

    public FocusOverlayWindow(IAppIdentifier launcher, Action<AllowedAppRule> onLaunched)
    {
        InitializeComponent();
        _launcher = launcher;
        _onLaunched = onLaunched;
        Title = "Anchor — Focus session";
    }

    public void UpdateContent(IReadOnlyList<AllowedAppRule> allowedRules, string? blockedAppName)
    {
        if (string.IsNullOrWhiteSpace(blockedAppName))
        {
            BlockedText.Visibility = Visibility.Collapsed;
        }
        else
        {
            // Calm, factual microcopy — say what stepped aside, not "you broke a
            // rule" (ANCHOR_BRAND.md §5). Sentence case; the mono style carries
            // the wide tracking.
            BlockedText.Text = $"Set aside just now: {blockedAppName}";
            BlockedText.Visibility = Visibility.Visible;
        }

        AllowedAppsPanel.Children.Clear();
        if (allowedRules.Count == 0)
        {
            AllowedAppsPanel.Children.Add(new TextBlock
            {
                Text = "No specific apps are set for this session — your teacher will guide what's next.",
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

    private static string FormatRule(AllowedAppRule rule) => rule.MatchKind switch
    {
        AllowedAppMatchKind.ProcessName => rule.Value,
        AllowedAppMatchKind.ExecutablePath => Path.GetFileNameWithoutExtension(rule.Value),
        AllowedAppMatchKind.Publisher => $"Apps from {rule.Value}",
        _ => rule.Value,
    };
}
