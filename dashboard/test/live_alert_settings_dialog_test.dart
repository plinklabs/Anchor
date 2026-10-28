import 'package:anchor_dashboard/alerts/alert_sound.dart';
import 'package:anchor_dashboard/alerts/live_alert_cues.dart';
import 'package:anchor_dashboard/alerts/live_alert_services.dart';
import 'package:anchor_dashboard/alerts/live_alert_settings.dart';
import 'package:anchor_dashboard/alerts/live_alert_settings_dialog.dart';
import 'package:anchor_dashboard/l10n/app_localizations.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'support/fake_alert_popups.dart';

// #455: the Pop-ups switch in the Notifications dialog, and the browser's
// permission next to it. The dashboard asks only when the teacher turns
// pop-ups on; once the browser blocks them, the dialog says how to allow them,
// and reads the permission again when the teacher comes back to the tab.

const _popups = 'Show pop-ups';
const _names = 'Show student names and sites in pop-ups';
const _notAsked = 'Your browser asks for permission when you turn them on.';
const _allowed = 'Allowed in this browser.';
const _blocked = 'Blocked in this browser.';
const _unsupported = "This browser can't show pop-ups.";
const _howTo = 'To allow pop-ups in Edge or Chrome:';
const _windows =
    'Still no pop-ups? Check that notifications for your browser are on in '
    'Windows: Settings → System → Notifications.';

class _FakeSound implements AlertSound {
  int plays = 0;

  @override
  ValueListenable<bool> get blocked => ValueNotifier(false);

  @override
  void unlock() {}

  @override
  void play() => plays++;

  @override
  void dispose() {}
}

typedef _Dialog = ({
  FakeAlertPopups popups,
  LiveAlertSettings settings,
  _FakeSound sound,
});

Future<_Dialog> _open(
  WidgetTester tester, {
  String? browser = 'default',
  String answer = 'granted',
  LiveAlertSettings? settings,
}) async {
  final popups = FakeAlertPopups(browser: browser, answer: answer);
  final sound = _FakeSound();
  final services = LiveAlertServices(
    settings: settings ?? LiveAlertSettings(store: MemoryAlertSettingsStore()),
    sound: sound,
    popups: popups,
  );
  await tester.pumpWidget(
    MaterialApp(
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      home: Builder(
        builder: (context) => Scaffold(
          body: TextButton(
            onPressed: () => showLiveAlertSettings(context, services),
            child: const Text('Open'),
          ),
        ),
      ),
    ),
  );
  await tester.tap(find.text('Open'));
  await tester.pumpAndSettle();
  return (popups: popups, settings: services.settings, sound: sound);
}

SwitchListTile _switch(WidgetTester tester, String label) =>
    tester.widget<SwitchListTile>(find.widgetWithText(SwitchListTile, label));

/// The teacher leaves the tab for the browser's site settings and comes
/// back: the window loses focus and gets it again.
void _comeBack(WidgetTester tester) {
  tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.inactive);
  tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.resumed);
}

void main() {
  testWidgets('pop-ups are off and nothing is asked until the teacher turns '
      'them on; then the browser asks once, and the answer shows', (
    tester,
  ) async {
    final d = await _open(tester);
    expect(_switch(tester, _popups).value, isFalse);
    expect(find.text(_notAsked), findsOneWidget);
    expect(d.popups.asked, 0, reason: 'opening the dialog asks nothing');
    expect(
      _switch(tester, _names).onChanged,
      isNull,
      reason: 'names only matter with pop-ups on',
    );

    await tester.tap(find.text(_popups));
    await tester.pumpAndSettle();

    expect(d.popups.asked, 1);
    expect(d.settings.popups, isTrue);
    expect(_switch(tester, _popups).value, isTrue);
    expect(find.text(_allowed), findsOneWidget);
    expect(find.text(_windows), findsOneWidget);
    expect(find.text(_howTo), findsNothing);
    expect(_switch(tester, _names).onChanged, isNotNull);

    // Off and on again: the browser has its answer, nothing to ask.
    await tester.tap(find.text(_popups));
    await tester.pumpAndSettle();
    expect(d.settings.popups, isFalse);
    expect(find.text(_allowed), findsOneWidget, reason: 'still allowed');
    await tester.tap(find.text(_popups));
    await tester.pumpAndSettle();
    expect(d.popups.asked, 1);
    expect(d.settings.popups, isTrue);
  });

  testWidgets('closing the prompt without an answer leaves pop-ups off, and '
      'turning them on asks again', (tester) async {
    final d = await _open(tester, answer: 'default');

    await tester.tap(find.text(_popups));
    await tester.pumpAndSettle();
    expect(d.popups.asked, 1);
    expect(d.settings.popups, isFalse);
    expect(_switch(tester, _popups).value, isFalse);
    expect(find.text(_notAsked), findsOneWidget);

    d.popups.answer = 'granted';
    await tester.tap(find.text(_popups));
    await tester.pumpAndSettle();
    expect(d.popups.asked, 2);
    expect(find.text(_allowed), findsOneWidget);
  });

  testWidgets('blocked: the dialog says so with the steps to allow them; '
      'after the teacher allows them in the browser and comes back, it shows '
      'them allowed', (tester) async {
    final d = await _open(tester, answer: 'denied');

    await tester.tap(find.text(_popups));
    await tester.pumpAndSettle();
    expect(find.text(_blocked), findsOneWidget);
    expect(_switch(tester, _popups).value, isTrue, reason: 'still wanted');
    expect(find.text(_howTo), findsOneWidget);
    for (final step in [
      '1. Click the icon left of the address bar.',
      '2. Set Notifications to Allow.',
      '3. Reload the page.',
    ]) {
      expect(find.text(step), findsOneWidget, reason: step);
    }
    expect(find.text(_windows), findsOneWidget);

    // In the browser's site settings, with the dialog still open.
    d.popups.browser = 'granted';
    await tester.pump();
    expect(find.text(_blocked), findsOneWidget, reason: 'not seen yet');
    _comeBack(tester);
    await tester.pumpAndSettle();

    expect(find.text(_allowed), findsOneWidget);
    expect(find.text(_howTo), findsNothing);
    expect(d.popups.asked, 1, reason: 'the browser is not asked again');
  });

  testWidgets('a blocked browser is read again when the dialog opens', (
    tester,
  ) async {
    final settings = LiveAlertSettings(store: MemoryAlertSettingsStore())
      ..popups = true;
    final d = await _open(tester, browser: 'denied', settings: settings);
    expect(find.text(_blocked), findsOneWidget);
    expect(find.text(_howTo), findsOneWidget);
    await tester.tap(find.text('Close'));
    await tester.pumpAndSettle();

    d.popups.browser = 'granted';
    await tester.tap(find.text('Open'));
    await tester.pumpAndSettle();
    expect(find.text(_allowed), findsOneWidget);
    expect(d.popups.asked, 0);
  });

  testWidgets('a browser that forgot its answer shows pop-ups off, and '
      'turning them on asks again', (tester) async {
    final settings = LiveAlertSettings(store: MemoryAlertSettingsStore())
      ..popups = true;
    final d = await _open(tester, settings: settings);
    expect(_switch(tester, _popups).value, isFalse);
    expect(find.text(_notAsked), findsOneWidget);

    await tester.tap(find.text(_popups));
    await tester.pumpAndSettle();
    expect(d.popups.asked, 1);
    expect(find.text(_allowed), findsOneWidget);
  });

  testWidgets('a browser without pop-ups says so, and the switch stays off', (
    tester,
  ) async {
    final d = await _open(tester, browser: null);
    expect(find.text(_unsupported), findsOneWidget);
    expect(_switch(tester, _popups).onChanged, isNull);
    await tester.tap(find.text(_popups));
    await tester.pumpAndSettle();
    expect(d.popups.asked, 0);
    expect(d.settings.popups, isFalse);
  });

  testWidgets('the names switch turns names on while pop-ups are on', (
    tester,
  ) async {
    final d = await _open(tester, browser: 'granted');
    await tester.tap(find.text(_popups));
    await tester.pumpAndSettle();
    expect(_switch(tester, _names).value, isFalse);

    await tester.tap(find.text(_names));
    await tester.pumpAndSettle();
    expect(d.settings.popupNames, isTrue);
    expect(_switch(tester, _names).value, isTrue);
  });

  testWidgets('Test plays the sound, and shows a sample pop-up while pop-ups '
      'are on', (tester) async {
    final d = await _open(tester, browser: 'granted');
    await tester.tap(find.byKey(const Key('alerts-test')));
    await tester.pumpAndSettle();
    expect(d.sound.plays, 1);
    expect(d.popups.shown, isEmpty, reason: 'pop-ups are off');

    await tester.tap(find.text(_popups));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('alerts-test')));
    await tester.pumpAndSettle();
    expect(d.sound.plays, 2);
    final sample = d.popups.shown.single;
    expect(sample.tag, alertPopupTestTag);
    expect(sample.title, 'Test pop-up');
    expect(sample.body, 'Alerts from a live session show up like this.');

    d.popups.click(alertPopupTestTag);
    expect(d.popups.broughtToFront, 1);
    expect(d.popups.onScreen, isEmpty);
  });
}
