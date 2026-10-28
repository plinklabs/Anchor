import 'package:flutter/foundation.dart';

import 'alert_sound.dart';
import 'live_alert_settings.dart';

/// Non-web fallback, so the conditional import in `alert_platform.dart`
/// resolves on the Dart VM (unit and widget tests) without `package:web`.
AlertSound createAlertSound() => _SilentAlertSound();

AlertSettingsStore createAlertSettingsStore() => MemoryAlertSettingsStore();

class _SilentAlertSound implements AlertSound {
  final _blocked = ValueNotifier<bool>(false);

  @override
  ValueListenable<bool> get blocked => _blocked;

  @override
  void unlock() {}

  @override
  void play() {}

  @override
  void dispose() => _blocked.dispose();
}
