import 'package:flutter/foundation.dart';

import 'alert_popups.dart';
import 'alert_sound.dart';
import 'live_alert_settings.dart';

/// Non-web fallback, so the conditional import in `alert_platform.dart`
/// resolves on the Dart VM (unit and widget tests) without `package:web`.
AlertSound createAlertSound() => _SilentAlertSound();

AlertSettingsStore createAlertSettingsStore() => MemoryAlertSettingsStore();

AlertPopups createAlertPopups() => _NoPopups();

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

/// No browser, so no pop-ups.
class _NoPopups implements AlertPopups {
  final _permission = ValueNotifier<PopupPermission>(
    PopupPermission.unsupported,
  );

  @override
  ValueListenable<PopupPermission> get permission => _permission;

  @override
  void refresh() {}

  @override
  Future<PopupPermission> request() async => PopupPermission.unsupported;

  @override
  void show(AlertPopup popup) {}

  @override
  void close(String tag) {}

  @override
  void bringPageToFront() {}

  @override
  void dispose() => _permission.dispose();
}
