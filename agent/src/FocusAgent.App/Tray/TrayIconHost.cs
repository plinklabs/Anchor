using FocusAgent.App.Localization;
using FocusAgent.Core.Realtime;
using H.NotifyIcon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace FocusAgent.App.Tray;

internal sealed class TrayIconHost : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly System.Drawing.Icon _trayIcon;
    private readonly MenuFlyout _menu;
    private readonly MenuFlyoutItem _statusItem;
    private readonly MenuFlyoutItem _joinByCodeItem;
    private readonly Func<bool> _canJoinByCode;
    private readonly DispatcherQueue _dispatcher;

    public TrayIconHost(
        Action onOpen,
        Action onJoinByCode,
        Func<bool> canJoinByCode,
        Action onQuit,
        DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _canJoinByCode = canJoinByCode;

        // AA4 (#176): the tray menu wears the DS ink treatment so it reads as one
        // piece with the rest of the agent — ink surface, Space Mono status,
        // on-ink actions, hairline rules, the one magenta spark on "Open Anchor".
        // Built by the shared TrayMenu factory so the real tray and the
        // --show-test-traymenu self-test render byte-for-byte the same menu.
        var menu = TrayMenu.Build(onOpen, onJoinByCode, onQuit);
        _menu = menu.Flyout;
        _statusItem = menu.StatusItem;
        _joinByCodeItem = menu.JoinItem;

        // Recompute Join-by-code's enabled state every time the menu opens
        // rather than threading SessionCoordinator events through here — it's
        // a one-shot read at exactly the moment the user is looking at it.
        _menu.Opening += (_, _) => _joinByCodeItem.IsEnabled = _canJoinByCode();

        // The Anchor mark (on-ink indigo, transparent) so it floats on the
        // taskbar — replaces the old programmatic "F" tile. Generated from the
        // brand mark; see design/icons/. Loaded as a System.Drawing.Icon and
        // set via Icon (not IconSource): IconSource decodes the bitmap
        // asynchronously, so ForceCreate() below would fire before the image
        // resolved and the tray would show an empty slot. The .ico is copied
        // next to the exe (see FocusAgent.App.csproj), so AppContext.BaseDirectory
        // resolves it in both the unpackaged dev run and the packaged install.
        _trayIcon = new System.Drawing.Icon(
            Path.Combine(AppContext.BaseDirectory, "Assets", "TrayIcon.ico"));

        _icon = new TaskbarIcon
        {
            ToolTipText = "Anchor",
            ContextFlyout = _menu,
            Icon = _trayIcon,
        };
    }

    public void Show() => _icon.ForceCreate();

    public void UpdateStatus(AgentConnectionState state, string? displayName)
    {
        // Space Mono eyebrow voice: UPPERCASE, plain, calm (ANCHOR_BRAND.md §5).
        var text = state switch
        {
            AgentConnectionState.Connected when !string.IsNullOrWhiteSpace(displayName) => Loc.Format("Tray_Status_ConnectedName", displayName!.ToUpperInvariant()),
            AgentConnectionState.Connected => Loc.Get("Tray_Status_Connected"),
            AgentConnectionState.Connecting => Loc.Get("Tray_Status_Connecting"),
            AgentConnectionState.Reconnecting => Loc.Get("Tray_Status_Reconnecting"),
            AgentConnectionState.Disconnected => Loc.Get("Tray_Status_Disconnected"),
            _ => TrayMenu.DefaultStatusText,
        };
        // The tooltip stays sentence-case prose ("Anchor — Connected"); only the
        // in-menu status row carries the mono eyebrow treatment.
        var tooltip = state switch
        {
            AgentConnectionState.Connected when !string.IsNullOrWhiteSpace(displayName) => Loc.Format("Tray_Tooltip_ConnectedAs", displayName!),
            AgentConnectionState.Connected => Loc.Get("Tray_Tooltip_Connected"),
            AgentConnectionState.Connecting => Loc.Get("Tray_Tooltip_Connecting"),
            AgentConnectionState.Reconnecting => Loc.Get("Tray_Tooltip_Reconnecting"),
            AgentConnectionState.Disconnected => Loc.Get("Tray_Tooltip_Disconnected"),
            _ => Loc.Get("Tray_Tooltip_SignedOut"),
        };
        _dispatcher.TryEnqueue(() =>
        {
            _statusItem.Text = text;
            _icon.ToolTipText = Loc.Format("Tray_Tooltip_Prefix", tooltip);
        });
    }

    public void Dispose()
    {
        _icon.Dispose();
        _trayIcon.Dispose();
    }
}
