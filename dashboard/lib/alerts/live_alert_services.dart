import 'alert_platform.dart';
import 'alert_sound.dart';
import 'live_alert_settings.dart';

/// What live-session alerts (#454) share across the app: the teacher's
/// settings and the browser's sound. One of each for the whole app, so the
/// Notifications dialog in the app bar and the live page agree, and a click
/// anywhere unlocks the one sound.
///
/// Production builds the browser's own ([LiveAlertServices.browser]); a test
/// passes fakes to `AnchorDashboard` instead, as it does the hub.
class LiveAlertServices {
  LiveAlertServices({
    required this.settings,
    required this.sound,
    this.clock = DateTime.now,
  });

  /// `localStorage` settings and Web Audio, on the web build.
  factory LiveAlertServices.browser() => LiveAlertServices(
    settings: LiveAlertSettings(store: createAlertSettingsStore()),
    sound: createAlertSound(),
  );

  final LiveAlertSettings settings;
  final AlertSound sound;

  /// Now, for the burst window. A test moves it on instead of waiting.
  final DateTime Function() clock;
}
