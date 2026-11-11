using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// End-to-end coverage for #423: the agent shows its version where a student, a
/// teacher or IT can read it — the main window and the tray menu — and it is the
/// version the agent reports on /status, i.e. the committed &lt;VersionPrefix&gt;
/// in <c>agent/Directory.Build.props</c> (<see cref="AgentVersionTests"/> pins /status
/// to the same value).
///
/// It drives the real exe's <c>--show-test-mainwindow</c> and
/// <c>--show-test-traymenu</c> self-tests, which build the production
/// <c>MainWindow</c> and the shared <c>TrayMenu</c> (the one <c>TrayIconHost</c>
/// uses), and reads them through UI Automation: what is on the surface, in the
/// language it is shown in, and where WinUI laid it out — not a lookup of the
/// string. Only the built exe shows that the stamped assembly version reaches the
/// composed surfaces in both catalogues. UI Automation holds on a headless runner,
/// so like <see cref="TrayMenuExitTests"/> this runs in the blocking suite, in the
/// serial visual collection so its windows never cover a capture.
/// </summary>
[Collection(VisualE2ECollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class AgentVersionShownTests
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(15);

    [Theory]
    [InlineData("en-US", "Version")]
    [InlineData("nl-NL", "Versie")]
    public async Task The_main_window_shows_the_version_and_lets_it_be_copied(string language, string label)
    {
        var expected = $"{label} {TestConfig.ExpectedAgentVersion}";

        await using var agent = AgentSelfTestProcess.Launch(
            AgentSelfTestProcess.ShowTestMainWindowArg, extraArgs: ["--ui-language", language]);
        await agent.WaitForWindowAsync(ReadTimeout);

        var (window, version, texts) = await WaitForTextAsync(agent.Pid, expected);
        Assert.True(
            version is not null,
            $"The main window shows no \"{expected}\". Texts on it: {Describe(texts)}.");

        // On screen, inside the window — not collapsed, clipped or scrolled away.
        var bounds = version!.Current.BoundingRectangle;
        Assert.False(version.Current.IsOffscreen, $"\"{expected}\" is off screen at {bounds}.");
        Assert.True(
            window.Contains(bounds),
            $"\"{expected}\" at {bounds} does not sit inside the main window at {window}.");

        // Selectable, so it can be copied into a message to IT.
        Assert.True(
            version.TryGetCurrentPattern(TextPattern.Pattern, out var pattern) &&
            ((TextPattern)pattern).SupportedTextSelection != SupportedTextSelection.None,
            $"\"{expected}\" on the main window can't be selected, so it can't be copied.");
    }

    [Theory]
    [InlineData("en-US", "Version", "Open Anchor")]
    [InlineData("nl-NL", "Versie", "Anchor openen")]
    public async Task The_tray_menu_shows_the_version_above_its_actions(string language, string label, string openAnchor)
    {
        var expected = $"{label} {TestConfig.ExpectedAgentVersion}";

        await using var agent = AgentSelfTestProcess.Launch(
            AgentSelfTestProcess.ShowTestTrayMenuArg, extraArgs: ["--ui-language", language]);
        await agent.WaitForWindowAsync(ReadTimeout);

        var items = await ReadOpenMenuAsync(agent.Pid, openAnchor);

        Assert.True(
            items.TryGetValue(expected, out var version),
            $"The tray menu shows no \"{expected}\". Items: {Describe(items)}.");
        // A line to read, not something to click.
        Assert.False(version!.Enabled, $"\"{expected}\" is an enabled menu item. Items: {Describe(items)}.");
        // In the menu's header, with the status line, above the first action.
        Assert.True(
            version.Bounds.Bottom <= items[openAnchor].Bounds.Top,
            $"\"{expected}\" at {version.Bounds} is not above \"{openAnchor}\". Items: {Describe(items)}.");
    }

    // The main window's bounds and the Text element named exactly `name`, polled
    // until it shows (the self-test renders a moment after its HWND appears). Also
    // returns every text seen, for a failure message.
    private static async Task<(Rect Window, AutomationElement? Match, IReadOnlyList<string> Texts)> WaitForTextAsync(
        int pid, string name)
    {
        var deadline = DateTime.UtcNow + ReadTimeout;
        var window = Rect.Empty;
        IReadOnlyList<string> texts = [];
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var root = AutomationElement.RootElement.FindFirst(
                    TreeScope.Children,
                    new PropertyCondition(AutomationElement.ProcessIdProperty, pid));
                if (root is not null)
                {
                    window = root.Current.BoundingRectangle;
                    var found = root.FindAll(
                        TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
                    var names = new List<string>();
                    foreach (AutomationElement text in found)
                    {
                        names.Add(text.Current.Name);
                        if (text.Current.Name == name) return (window, text, names);
                    }
                    texts = names;
                }
            }
            catch (ElementNotAvailableException)
            {
                // The tree changed under the read; read again.
            }
            await Task.Delay(200);
        }
        return (window, null, texts);
    }

    private sealed record MenuItemInfo(bool Enabled, Rect Bounds);

    // The open tray menu's items by name, once it is open with `openAnchor` in the
    // tree and two reads in a row agree (the self-test re-opens it on a timer, so a
    // read can catch it closing).
    private static async Task<IReadOnlyDictionary<string, MenuItemInfo>> ReadOpenMenuAsync(int pid, string openAnchor)
    {
        var deadline = DateTime.UtcNow + ReadTimeout;
        Dictionary<string, MenuItemInfo>? previous = null, items = null;
        while (DateTime.UtcNow < deadline)
        {
            items = ReadMenu(pid);
            if (items is not null && items.ContainsKey(openAnchor) &&
                previous is not null && Describe(items) == Describe(previous))
                return items;
            previous = items;
            await Task.Delay(200);
        }
        throw new TimeoutException(
            $"The tray menu self-test never showed a settled open menu with \"{openAnchor}\" within " +
            $"{ReadTimeout.TotalSeconds:N0}s. Last read: {(items is null ? "<no menu>" : Describe(items))}.");
    }

    private static Dictionary<string, MenuItemInfo>? ReadMenu(int pid)
    {
        try
        {
            var windows = AutomationElement.RootElement.FindAll(
                TreeScope.Children,
                new PropertyCondition(AutomationElement.ProcessIdProperty, pid));
            foreach (AutomationElement window in windows)
            {
                var menu = window.FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Menu));
                if (menu is null) continue;

                var items = new Dictionary<string, MenuItemInfo>();
                var isMenuItem = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem);
                foreach (AutomationElement item in menu.FindAll(TreeScope.Descendants, isMenuItem))
                    items[item.Current.Name] = new MenuItemInfo(item.Current.IsEnabled, item.Current.BoundingRectangle);
                return items;
            }
        }
        catch (ElementNotAvailableException)
        {
            // The self-test re-opens the menu on a timer; it can vanish mid-read.
        }
        return null;
    }

    private static string Describe(IReadOnlyList<string> texts) =>
        texts.Count == 0 ? "<none>" : string.Join(", ", texts.Select(t => $"\"{t}\""));

    private static string Describe(IReadOnlyDictionary<string, MenuItemInfo> items) =>
        items.Count == 0
            ? "<no items>"
            : string.Join(", ", items.Select(i => $"\"{i.Key}\"{(i.Value.Enabled ? "" : " (disabled)")} {i.Value.Bounds}"));
}
