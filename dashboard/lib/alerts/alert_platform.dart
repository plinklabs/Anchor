import 'alert_platform_stub.dart'
    if (dart.library.js_interop) 'alert_platform_web.dart'
    as platform;
import 'alert_popups.dart';
import 'alert_sound.dart';
import 'live_alert_settings.dart';

/// The browser's sound for live-session alerts (#454): Web Audio on the web
/// build, silence elsewhere (the dashboard only ships to the web; VM tests
/// inject their own).
AlertSound createAlertSound() => platform.createAlertSound();

/// Where the alert settings are kept: the browser's `localStorage` on the web
/// build, memory elsewhere.
AlertSettingsStore createAlertSettingsStore() =>
    platform.createAlertSettingsStore();

/// The browser's system pop-ups (#455): its Notification API on the web
/// build; elsewhere there are none.
AlertPopups createAlertPopups() => platform.createAlertPopups();
