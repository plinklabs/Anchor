import 'dart:convert';

import 'package:flutter/foundation.dart';

import 'alert_platform.dart';
import 'live_alerts.dart';

/// Where the teacher's alert settings are kept: per browser (#454), in
/// `localStorage` on the web build. Nothing goes to the backend.
abstract class AlertSettingsStore {
  /// The settings as last written, or null when there are none (or the
  /// browser won't give them).
  String? read();

  /// Keeps [value]. A browser that refuses storage just forgets it.
  void write(String value);
}

/// Keeps the settings in memory only, for tests and non-web builds.
class MemoryAlertSettingsStore implements AlertSettingsStore {
  MemoryAlertSettingsStore([this.value]);

  String? value;

  @override
  String? read() => value;

  @override
  void write(String value) => this.value = value;
}

/// What the teacher chose in the Notifications dialog (#454): whether alerts
/// make a sound, and which [LiveAlertCategory]s alert at all. Everything is on
/// until they switch it off, and their choices survive a reload.
///
/// Stored as one JSON object, so a later setting (the pop-ups of #455) is one
/// more key. Keys this version doesn't know are kept as they are.
class LiveAlertSettings extends ChangeNotifier {
  LiveAlertSettings({AlertSettingsStore? store})
    : _store = store ?? createAlertSettingsStore() {
    _load();
  }

  /// The `localStorage` key.
  static const String storageKey = 'anchor.liveAlerts';

  static const String _soundKey = 'sound';
  static const String _categoriesKey = 'categories';

  final AlertSettingsStore _store;
  Map<String, Object?> _values = {};

  /// Whether an alert plays a sound.
  bool get sound => _values[_soundKey] != false;
  set sound(bool on) {
    if (on == sound) return;
    _values[_soundKey] = on;
    _save();
  }

  /// Whether events of [category] alert at all.
  bool isOn(LiveAlertCategory category) => _categories[category.name] != false;

  /// Switches [category] on or off.
  void setOn(LiveAlertCategory category, bool on) {
    if (on == isOn(category)) return;
    _values[_categoriesKey] = {..._categories, category.name: on};
    _save();
  }

  /// Whether any category alerts.
  bool get anyOn => LiveAlertCategory.values.any(isOn);

  Map<String, Object?> get _categories {
    final Object? categories = _values[_categoriesKey];
    return categories is Map<String, Object?> ? categories : const {};
  }

  void _load() {
    try {
      final String? raw = _store.read();
      if (raw == null) return;
      final Object? decoded = jsonDecode(raw);
      if (decoded is Map<String, Object?>) _values = decoded;
    } catch (_) {
      // Unreadable or refused: the defaults, everything on.
    }
  }

  void _save() {
    try {
      _store.write(jsonEncode(_values));
    } catch (_) {
      // The browser refused storage: the choice holds until a reload.
    }
    notifyListeners();
  }
}
