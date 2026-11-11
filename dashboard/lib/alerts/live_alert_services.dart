import 'alert_platform.dart';
import 'alert_popups.dart';
import 'alert_sound.dart';
import 'live_alert_settings.dart';

/// What live-session alerts (#454) share across the app: the teacher's
/// settings, the browser's sound and its system pop-ups (#455). One of each
/// for the whole app, so the Notifications dialog in the app bar and the live
/// page agree, and a click anywhere unlocks the one sound.
///
/// Production builds the browser's own ([LiveAlertServices.browser]); a test
/// passes fakes to `AnchorDashboard` instead, as it does the hub.
class LiveAlertServices {
  LiveAlertServices({
    required this.settings,
    required this.sound,
    AlertPopups? popups,
    this.clock = DateTime.now,
  }) : popups = popups ?? createAlertPopups();

  /// `localStorage` settings, Web Audio and the Notification API, on the web
  /// build.
  factory LiveAlertServices.browser() => LiveAlertServices(
    settings: LiveAlertSettings(store: createAlertSettingsStore()),
    sound: createAlertSound(),
    popups: createAlertPopups(),
  );

  final LiveAlertSettings settings;
  final AlertSound sound;

  /// The browser's system pop-ups. Null in the constructor takes the
  /// browser's own (none off the web).
  final AlertPopups popups;

  /// Now, for the burst window. A test moves it on instead of waiting.
  final DateTime Function() clock;
}
