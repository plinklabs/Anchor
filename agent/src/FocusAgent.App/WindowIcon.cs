using System.Runtime.Versioning;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace FocusAgent.App;

/// <summary>
/// Gives an agent window the Anchor mark as its icon (#414), in its title bar, on its
/// taskbar button and in Alt+Tab. A WinUI 3 window doesn't take the icon embedded in
/// the exe (<c>ApplicationIcon</c>): unless it sets one, Windows draws the generic
/// "blank app" icon. Every agent window calls <see cref="Apply"/> as it is created,
/// except the crash dialog, which sets the same file inline so it depends on nothing
/// shared.
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
internal static class WindowIcon
{
    /// <summary>
    /// The Anchor mark next to the exe (copied there by the csproj). The tray uses
    /// it, <c>vpk pack --icon</c> brands the installer with it, and the exe embeds it.
    /// </summary>
    public static string IconPath { get; } = Path.Combine(AppContext.BaseDirectory, "Assets", "TrayIcon.ico");

    /// <summary>
    /// Set the Anchor icon on <paramref name="window"/>. Best effort: a missing or
    /// unreadable icon file leaves the default icon rather than failing the window.
    /// </summary>
    public static void Apply(Window window)
    {
        try
        {
            var windowId = Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(window));
            AppWindow.GetFromWindowId(windowId).SetIcon(IconPath);
        }
        catch
        {
            // Only the icon is lost: the window still shows, with Windows' default.
        }
    }
}
