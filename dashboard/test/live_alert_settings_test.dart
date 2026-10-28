import 'dart:convert';

import 'package:anchor_dashboard/alerts/live_alert_settings.dart';
import 'package:anchor_dashboard/alerts/live_alerts.dart';
import 'package:flutter_test/flutter_test.dart';

// #454: the teacher's alert settings, kept per browser.

class _RefusingStore implements AlertSettingsStore {
  @override
  String? read() => throw StateError('SecurityError');

  @override
  void write(String value) => throw StateError('QuotaExceededError');
}

void main() {
  test('everything is on until the teacher switches it off', () {
    final settings = LiveAlertSettings(store: MemoryAlertSettingsStore());

    expect(settings.sound, isTrue);
    for (final category in LiveAlertCategory.values) {
      expect(settings.isOn(category), isTrue, reason: category.name);
    }
    expect(settings.anyOn, isTrue);
  });

  test('the choices survive a reload: a new instance reads them back', () {
    final store = MemoryAlertSettingsStore();
    final settings = LiveAlertSettings(store: store);
    var notified = 0;
    settings.addListener(() => notified++);

    settings.sound = false;
    settings.setOn(LiveAlertCategory.tamper, false);
    // Unchanged: nothing to write or tell.
    settings.setOn(LiveAlertCategory.tamper, false);
    expect(notified, 2);

    final reloaded = LiveAlertSettings(store: store);
    expect(reloaded.sound, isFalse);
    expect(reloaded.isOn(LiveAlertCategory.tamper), isFalse);
    expect(reloaded.isOn(LiveAlertCategory.accessRequest), isTrue);
  });

  test('anyOn is false once every category is off', () {
    final settings = LiveAlertSettings(store: MemoryAlertSettingsStore());
    for (final category in LiveAlertCategory.values) {
      settings.setOn(category, false);
    }
    expect(settings.anyOn, isFalse);
  });

  test('pop-ups and the names in them are off until the teacher turns them '
      'on, and survive a reload (#455)', () {
    final store = MemoryAlertSettingsStore();
    final settings = LiveAlertSettings(store: store);
    expect(settings.popups, isFalse);
    expect(settings.popupNames, isFalse);
    var notified = 0;
    settings.addListener(() => notified++);

    settings.popups = true;
    settings.popupNames = true;
    // Unchanged: nothing to write or tell.
    settings.popups = true;
    expect(notified, 2);

    final reloaded = LiveAlertSettings(store: store);
    expect(reloaded.popups, isTrue);
    expect(reloaded.popupNames, isTrue);
    expect(reloaded.sound, isTrue, reason: 'the rest as it was');
    expect(jsonDecode(store.value!), {'popups': true, 'popupNames': true});

    reloaded.popups = false;
    expect(LiveAlertSettings(store: store).popups, isFalse);
  });

  test('keys it does not know are kept (a later setting, #455)', () {
    final store = MemoryAlertSettingsStore(
      jsonEncode({'sound': true, 'popups': true}),
    );
    LiveAlertSettings(store: store).sound = false;

    expect(jsonDecode(store.value!), {'sound': false, 'popups': true});
  });

  test('unreadable or refused storage leaves the defaults, and a choice '
      'still holds until a reload', () {
    final garbled = LiveAlertSettings(
      store: MemoryAlertSettingsStore('not json'),
    );
    expect(garbled.sound, isTrue);

    final refused = LiveAlertSettings(store: _RefusingStore());
    expect(refused.sound, isTrue);
    refused.setOn(LiveAlertCategory.agentLost, false);
    expect(refused.isOn(LiveAlertCategory.agentLost), isFalse);
  });
}
