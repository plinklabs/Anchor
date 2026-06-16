using System.Runtime.Versioning;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace FocusAgent.App.Diagnostics;

/// <summary>
/// The last-resort crash dialog (#248): a self-contained WinUI window shown when
/// the agent hits a fatal error, instead of the process vanishing silently (the
/// #247 "opens and closes instantly").
///
/// It deliberately builds its whole control tree in code with <em>explicit</em>
/// brushes — no XAML file, no design-system resource lookups, no DI — so it can
/// still render when the very things a normal window leans on (the host, config,
/// the merged resource dictionary, logging) are what failed. The detail field is
/// a read-only but fully selectable <see cref="TextBox"/> the user can select-all
/// + copy; a Copy button copies the whole diagnostic block in one click, and the
/// full stack sits behind a collapsed "Show technical details" expander so a
/// student isn't shown a raw stack dump by default.
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
internal sealed class CrashReportWindow : Window
{
    // Explicit ink palette (mirrors the DS ink tokens, ANCHOR_BRAND.md §3) so the
    // window matches the rest of the agent without depending on the merged DS
    // dictionary having loaded — the crash path can't assume it did.
    private static readonly Color Ink = Color.FromArgb(0xFF, 0x1B, 0x1B, 0x23);
    private static readonly Color OnInk = Color.FromArgb(0xFF, 0xF4, 0xF4, 0xF6);
    private static readonly Color Muted = Color.FromArgb(0xFF, 0x9A, 0x9A, 0xA6);
    private static readonly Color Magenta = Color.FromArgb(0xFF, 0xDB, 0x27, 0x77);
    private static readonly Color FieldBg = Color.FromArgb(0xFF, 0x26, 0x26, 0x30);

    private const int WidthDip = 560;
    private const int HeightDip = 480;

    private readonly string _clipboardText;

    /// <param name="headline">The friendly top line (e.g. "Anchor couldn't start").</param>
    /// <param name="detail">The curated, selectable detail block.</param>
    /// <param name="fullStack">The full exception text for the expander.</param>
    public CrashReportWindow(string headline, string detail, string fullStack)
    {
        Title = "Anchor";
        _clipboardText = $"{detail}{Environment.NewLine}{Environment.NewLine}" +
                         $"--- technical details ---{Environment.NewLine}{fullStack}";

        Content = BuildContent(headline, detail, fullStack);
    }

    private FrameworkElement BuildContent(string headline, string detail, string fullStack)
    {
        var root = new Grid
        {
            Background = new SolidColorBrush(Ink),
            Padding = new Thickness(24),
            RowSpacing = 14,
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // headline
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // intro
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // detail
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // expander
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // buttons

        var title = new TextBlock
        {
            Text = headline,
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(OnInk),
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetRow(title, 0);

        var intro = new TextBlock
        {
            Text = "You can copy the details below and send them to us so we can help.",
            Foreground = new SolidColorBrush(Muted),
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetRow(intro, 1);

        // AcceptsReturn / TextWrapping must be set BEFORE Text: a TextBox processes
        // the assigned Text against its current single-/multi-line mode, so setting
        // Text first (while AcceptsReturn is still the default false) collapses it to
        // the first line and a later AcceptsReturn = true won't re-expand it.
        var detailField = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            IsSpellCheckEnabled = false,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            Foreground = new SolidColorBrush(OnInk),
            Background = new SolidColorBrush(FieldBg),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Stretch,
            Text = ForDisplay(detail),
        };
        Grid.SetRow(detailField, 2);

        var stackField = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            IsSpellCheckEnabled = false,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Foreground = new SolidColorBrush(Muted),
            Background = new SolidColorBrush(FieldBg),
            BorderThickness = new Thickness(0),
            MaxHeight = 160,
            Text = ForDisplay(fullStack),
        };

        var expander = new Expander
        {
            Header = "Show technical details",
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Foreground = new SolidColorBrush(OnInk),
            Content = stackField,
        };
        Grid.SetRow(expander, 3);

        var copyButton = new Button
        {
            Content = "Copy",
            Background = new SolidColorBrush(Magenta),
            Foreground = new SolidColorBrush(Colors.White),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(20, 8, 20, 8),
        };
        copyButton.Click += (_, _) => CopyToClipboard();

        var closeButton = new Button
        {
            Content = "Close",
            Foreground = new SolidColorBrush(OnInk),
            Padding = new Thickness(20, 8, 20, 8),
        };
        closeButton.Click += (_, _) => Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
        };
        buttons.Children.Add(copyButton);
        buttons.Children.Add(closeButton);
        Grid.SetRow(buttons, 4);

        root.Children.Add(title);
        root.Children.Add(intro);
        root.Children.Add(detailField);
        root.Children.Add(expander);
        root.Children.Add(buttons);
        return root;
    }

    // WinUI's multiline TextBox uses a bare CR as its native line break: text set
    // with CRLF (what Environment.NewLine produces) renders only the first line.
    // Normalise to CR so every line shows. The Copy button still puts the original
    // CRLF text on the clipboard, so what the user pastes back reads correctly.
    private static string ForDisplay(string text) =>
        text.Replace("\r\n", "\r").Replace('\n', '\r');

    private void CopyToClipboard()
    {
        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(_clipboardText);
            Clipboard.SetContent(package);
        }
        catch
        {
            // The field is selectable for manual copy as a fallback; a clipboard
            // failure must never throw out of the crash dialog.
        }
    }

    /// <summary>
    /// Size to a fixed, centred box and show with focus. Done in code (rather than
    /// via the shared <c>DialogWindowPositioner</c>) so the crash window keeps its
    /// no-shared-dependency promise — it is the surface of last resort.
    /// </summary>
    public void ConfigureAndShow()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        var dpi = GetDpiForWindow(hwnd);
        if (dpi == 0) dpi = 96;
        var scale = dpi / 96.0;
        var width = (int)(WidthDip * scale);
        var height = (int)(HeightDip * scale);

        var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
        var workArea = displayArea.WorkArea;
        var x = workArea.X + (workArea.Width - width) / 2;
        var y = workArea.Y + (workArea.Height - height) / 2;
        appWindow.MoveAndResize(new RectInt32(x, y, width, height));

        Activate();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
