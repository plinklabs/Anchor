using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Automation;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// End-to-end coverage for the tray menu side of #445: while the agent has a session
/// going on, the menu offers no "Exit Anchor". Drives the agent's
/// <c>--show-test-traymenu</c> self-test, which builds the real menu through the
/// shared <c>TrayMenu</c> factory (the one <c>TrayIconHost</c> uses) and opens it
/// over a host window; with <c>--traymenu-in-session</c> the menu is told no session
/// may be quit, the way the real tray's is mid-session, and decides as it opens.
///
/// It reads the open menu through UI Automation — what the student (and a screen
/// reader) is offered, and where WinUI laid it out — rather than pixels: a collapsed
/// item is not in the tree at all, a disabled one reports so. That holds on a
/// headless runner (and on a locked screen), so the spec runs in the blocking suite;
/// it shares the serial visual collection so its window never covers a capture or a
/// spec that watches the foreground. The rule itself, on a real agent in a real
/// session, is <see cref="QuitDuringSessionTests"/>.
/// </summary>
[Collection(VisualE2ECollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class TrayMenuExitTests
{
    // --ui-language en-US pins these, whatever the machine's display language.
    private const string OpenAnchor = "Open Anchor";
    private const string JoinASession = "Join a session…";
    private const string ExitAnchor = "Exit Anchor";

    // Layout rounding between two launches of the same menu: at most a pixel. A rule
    // left behind adds its own height (a 1 DIP line plus 1 DIP above and below: 3px
    // at 100% scaling, 4px at 125%), so it still fails.
    private const double LayoutTolerancePx = 1;

    [Fact]
    public async Task Outside_a_session_the_tray_menu_offers_Exit()
    {
        var menu = await ReadOpenMenuAsync(inSession: false);

        Assert.True(
            menu.Items.TryGetValue(ExitAnchor, out var exit) && exit.Enabled,
            $"The tray menu offers no enabled \"{ExitAnchor}\" outside a session. Items: {menu}.");
        Assert.True(menu.Items[JoinASession].Enabled, $"\"{JoinASession}\" is disabled outside a session. Items: {menu}.");
    }

    [Fact]
    public async Task During_a_session_the_tray_menu_offers_no_Exit_and_ends_on_its_last_item()
    {
        var inSession = await ReadOpenMenuAsync(inSession: true);

        Assert.False(
            inSession.Items.ContainsKey(ExitAnchor),
            $"The tray menu still offers \"{ExitAnchor}\" during a session. Items: {inSession}.");
        Assert.True(inSession.Items[OpenAnchor].Enabled, $"\"{OpenAnchor}\" is disabled during a session. Items: {inSession}.");
        Assert.True(
            inSession.Items.TryGetValue(JoinASession, out var join) && !join.Enabled,
            $"\"{JoinASession}\" should be shown disabled during a session. Items: {inSession}.");

        // The rule above Exit goes with it: the menu ends below its last item the
        // way the full menu ends below Exit, not on a stray hairline (which UI
        // Automation doesn't list, but which takes its height in the layout).
        var outside = await ReadOpenMenuAsync(inSession: false);
        var gapInSession = inSession.Bounds.Bottom - inSession.Items.Values.Max(i => i.Bounds.Bottom);
        var gapOutside = outside.Bounds.Bottom - outside.Items[ExitAnchor].Bounds.Bottom;
        Assert.True(
            Math.Abs(gapInSession - gapOutside) <= LayoutTolerancePx,
            $"During a session the tray menu ends {gapInSession:0}px below its last item, outside one " +
            $"{gapOutside:0}px below Exit; the rule above Exit may have stayed behind. " +
            $"In session: {inSession}. Outside: {outside}.");
    }

    private sealed record MenuItemInfo(bool Enabled, Rect Bounds);

    private sealed record OpenMenu(Rect Bounds, IReadOnlyDictionary<string, MenuItemInfo> Items)
    {
        public override string ToString() =>
            $"menu {Bounds}; " + (Items.Count == 0
                ? "<no items>"
                : string.Join(", ", Items.Select(i => $"\"{i.Key}\"{(i.Value.Enabled ? "" : " (disabled)")} {i.Value.Bounds}")));
    }

    // The open menu: its bounds and its items by name, with whether each is enabled
    // and where it is. Waits until the menu is open with its primary item in the
    // tree (the self-test opens it a moment after the host window shows), so a menu
    // not open yet can't pass for one without Exit, and until two reads in a row
    // agree, so a read caught while the self-test re-opens the menu doesn't count.
    private static async Task<OpenMenu> ReadOpenMenuAsync(bool inSession)
    {
        string[] extraArgs = inSession
            ? [AgentSelfTestProcess.TrayMenuInSessionArg, "--ui-language", "en-US"]
            : ["--ui-language", "en-US"];
        await using var agent = AgentSelfTestProcess.Launch(AgentSelfTestProcess.ShowTestTrayMenuArg, extraArgs: extraArgs);
        await agent.WaitForWindowAsync(TimeSpan.FromSeconds(15));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        OpenMenu? previous = null, menu = null;
        while (DateTime.UtcNow < deadline)
        {
            menu = ReadMenu(agent.Pid);
            if (menu is not null && menu.Items.ContainsKey(OpenAnchor) && menu.ToString() == previous?.ToString())
                return menu;
            previous = menu;
            await Task.Delay(200);
        }
        throw new TimeoutException(
            $"The tray menu self-test never showed a settled open menu with \"{OpenAnchor}\" within 15s. " +
            $"Last read: {menu?.ToString() ?? "<no menu>"}.");
    }

    private static OpenMenu? ReadMenu(int pid)
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
                return new OpenMenu(menu.Current.BoundingRectangle, items);
            }
        }
        catch (ElementNotAvailableException)
        {
            // The self-test re-opens the menu on a timer; it can vanish mid-read. The
            // caller reads again.
        }
        return null;
    }
}
