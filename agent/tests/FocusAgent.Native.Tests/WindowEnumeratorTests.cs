using System.Runtime.Versioning;
using FocusAgent.Native;

namespace FocusAgent.Native.Tests;

[SupportedOSPlatform("windows")]
public class WindowEnumeratorTests
{
    private const int WsExToolWindow = 0x00000080;
    private const int WsExAppWindow = 0x00040000;

    [Fact]
    public void IsCandidate_accepts_a_visible_titled_top_level_window()
    {
        // A normal app window: visible, not minimized, no owner, not a tool
        // window, has a title. This is what the session-start sweep (#104)
        // should consider for minimizing.
        Assert.True(WindowEnumerator.IsCandidate(
            visible: true, iconic: false, hasOwner: false, exStyle: WsExAppWindow, titleLength: 12));
    }

    [Theory]
    [InlineData(false, false, false, 0, 12)]               // not visible
    [InlineData(true, true, false, 0, 12)]                 // already minimized
    [InlineData(true, false, true, 0, 12)]                 // owned (dialog / tool popup)
    [InlineData(true, false, false, WsExToolWindow, 12)]   // tool window
    [InlineData(true, false, false, 0, 0)]                 // no title
    public void IsCandidate_rejects_windows_the_sweep_must_not_touch(
        bool visible, bool iconic, bool hasOwner, int exStyle, int titleLength)
    {
        Assert.False(WindowEnumerator.IsCandidate(visible, iconic, hasOwner, exStyle, titleLength));
    }

    [Fact]
    public void IsCandidate_rejects_a_window_that_is_both_app_and_tool_styled()
    {
        // The tool-window bit dominates even when WS_EX_APPWINDOW is also set —
        // a deliberately conservative filter so the sweep never minimizes shell
        // chrome that happens to carry both bits.
        Assert.False(WindowEnumerator.IsCandidate(
            visible: true, iconic: false, hasOwner: false,
            exStyle: WsExAppWindow | WsExToolWindow, titleLength: 8));
    }
}
