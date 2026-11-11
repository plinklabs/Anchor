import 'package:anchor_dashboard/alerts/live_alert_cues.dart';
import 'package:anchor_dashboard/alerts/live_alert_settings.dart';
import 'package:anchor_dashboard/alerts/live_alerts.dart';
import 'package:anchor_dashboard/l10n/app_localizations.dart';
import 'package:anchor_dashboard/realtime/session_hub_client.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';

import 'support/fake_alert_popups.dart';

// #455: the system pop-up for live-session alerts, as a consumer of
// LiveAlerts: one per burst of each kind while the teacher can't see the
// page, replaced rather than stacked, naming no one unless they ask.

const _session = 'session-1';

final AppLocalizations _en = lookupAppLocalizations(const Locale('en'));
final AppLocalizations _nl = lookupAppLocalizations(const Locale('nl'));

SessionEvent _event(String kind, Map<String, dynamic> payload) =>
    SessionEvent(kind: kind, payload: payload, at: DateTime(2026, 6, 12, 9));

SessionEvent _request(String user, String host) => _event('UnblockRequested', {
  'userId': user,
  'userDisplayName': user,
  'host': host,
});

SessionEvent _state(String user, String state) => _event(
  'ParticipantStateChanged',
  {'userId': user, 'displayName': user, 'state': state},
);

SessionEvent _lost(String user) => _event('HeartbeatLost', {'userId': user});

SessionEvent _tamper(String user) => _event('TamperDetected', {
  'userId': user,
  'userDisplayName': user,
  'kind': 'inprivate_opened',
});

class _Clock {
  DateTime now = DateTime(2026, 6, 12, 9);
  void advance(Duration by) => now = now.add(by);
  void afterTheBurst() => advance(const Duration(seconds: 11));
}

typedef _Cue = ({
  LiveAlerts alerts,
  LiveAlertSettings settings,
  FakeAlertPopups popups,
  _Clock clock,
  List<LiveAlertCategory> opened,
  AlertPopupCue cue,
});

_Cue _cue({
  bool popupsOn = true,
  String browser = 'granted',
  Map<String, String> roster = const {},
}) {
  final clock = _Clock();
  final settings = LiveAlertSettings(store: MemoryAlertSettingsStore())
    ..popups = popupsOn;
  final alerts = LiveAlerts(settings: settings, clock: () => clock.now)
    ..pageHidden = true;
  final popups = FakeAlertPopups(browser: browser);
  final opened = <LiveAlertCategory>[];
  final cue = AlertPopupCue(
    alerts: alerts,
    popups: popups,
    l10n: _en,
    scope: _session,
    nameOf: (id) => roster[id],
    onOpen: opened.add,
  );
  addTearDown(() {
    cue.dispose();
    alerts.dispose();
  });
  return (
    alerts: alerts,
    settings: settings,
    popups: popups,
    clock: clock,
    opened: opened,
    cue: cue,
  );
}

String _tag(LiveAlertCategory category) => alertPopupTag(_session, category);

void main() {
  test('no pop-up until the teacher turns pop-ups on', () {
    final c = _cue(popupsOn: false);
    c.alerts.handle(_request('ada', 'docs.example'));
    expect(c.popups.shown, isEmpty);

    c.settings.popups = true;
    c.clock.afterTheBurst();
    c.alerts.handle(_request('bo', 'docs.example'));
    expect(c.popups.shown, hasLength(1));
  });

  test('no pop-up while the teacher can see the page', () {
    final c = _cue();
    c.alerts.pageHidden = false;
    c.alerts.handle(_request('ada', 'docs.example'));
    expect(c.popups.shown, isEmpty);
  });

  test('twenty requests in a burst show one pop-up; the next burst replaces '
      'it, with the count of requests the teacher has not seen', () {
    final c = _cue();
    for (var i = 0; i < 20; i++) {
      c.alerts.handle(_request('student$i', 'docs.example'));
      c.clock.advance(const Duration(milliseconds: 250));
    }
    expect(c.popups.shown, hasLength(1));
    expect(c.popups.shown.single.title, 'New access request');
    expect(c.popups.shown.single.tag, _tag(LiveAlertCategory.accessRequest));

    c.clock.afterTheBurst();
    c.alerts.handle(_request('student20', 'docs.example'));
    expect(c.popups.shown, hasLength(2));
    expect(c.popups.shown.last.title, 'New access requests (21)');
    // Replaced, not stacked.
    expect(c.popups.onScreen.keys, [_tag(LiveAlertCategory.accessRequest)]);
  });

  test('each kind has its own pop-up, in a burst of another kind too', () {
    final c = _cue();
    c.alerts.handle(_request('ada', 'docs.example'));
    c.alerts.handle(_state('bo', 'Left'));
    c.alerts.handle(_lost('cas'));
    c.alerts.handle(_tamper('dina'));

    expect(c.popups.shown.map((p) => p.title), [
      'New access request',
      'A student left the session',
      "A student's agent stopped reporting",
      'Tampering detected',
    ]);
    expect(c.popups.onScreen, hasLength(4));
  });

  test('by default a pop-up names no student and no site; with names on, '
      'it does', () {
    final c = _cue(roster: {'cas': 'Cas'});
    c.alerts.handle(_request('Jana', 'docs.google.com'));
    final anonymous = c.popups.shown.single;
    expect(anonymous.title, 'New access request');
    expect(anonymous.body, isEmpty);

    c.settings.popupNames = true;
    c.clock.afterTheBurst();
    c.alerts.handle(_request('Piet', 'docs.google.com'));
    c.alerts.handle(_state('Ada', 'Left'));
    c.alerts.handle(_state('Bo', 'Declined'));
    c.clock.afterTheBurst();
    // HeartbeatLost carries only the id: the roster names the student.
    c.alerts.handle(_lost('cas'));
    c.alerts.handle(_tamper('Dina'));

    expect(c.popups.shown.skip(1).map((p) => (p.title, p.body)), [
      ('New access requests (2)', 'Piet asks for access to docs.google.com'),
      ('A student left the session', 'Ada left the session'),
      ("A student's agent stopped reporting", "Cas's agent stopped reporting"),
      ('Tampering detected', "Tampering detected on Dina's computer"),
    ]);
    expect(
      c.popups.onScreen[_tag(LiveAlertCategory.studentLeft)]!.body,
      'Ada left the session',
      reason: 'Bo declined within the burst',
    );
  });

  test('a student the page cannot name leaves the body empty', () {
    final c = _cue()..settings.popupNames = true;
    c.alerts.handle(_lost('unknown'));
    expect(c.popups.shown.single.body, isEmpty);
  });

  test('a declined session says so, with names on', () {
    final c = _cue()..settings.popupNames = true;
    c.alerts.handle(_state('Bo', 'Declined'));
    expect(c.popups.shown.single.body, 'Bo declined the session');
  });

  test('clicking a pop-up brings the dashboard to the front, closes it and '
      'opens its kind on the page', () {
    final c = _cue();
    c.alerts.handle(_request('ada', 'docs.example'));
    c.alerts.handle(_tamper('bo'));

    c.popups.click(_tag(LiveAlertCategory.accessRequest));
    expect(c.popups.broughtToFront, 1);
    expect(c.opened, [LiveAlertCategory.accessRequest]);
    expect(c.popups.onScreen.keys, [_tag(LiveAlertCategory.tamper)]);
  });

  test(
    'coming back to the page takes the pop-ups away; so does leaving it',
    () {
      final c = _cue();
      c.alerts.handle(_request('ada', 'docs.example'));
      c.alerts.handle(_tamper('bo'));
      expect(c.popups.onScreen, hasLength(2));

      c.alerts.pageHidden = false;
      expect(c.popups.onScreen, isEmpty);

      c.alerts.pageHidden = true;
      c.clock.afterTheBurst();
      c.alerts.handle(_state('cas', 'Left'));
      expect(c.popups.onScreen, hasLength(1));
      c.cue.dispose();
      expect(c.popups.onScreen, isEmpty);
    },
  );

  test('a browser that blocks pop-ups shows none', () {
    final c = _cue(browser: 'denied');
    c.alerts.handle(_request('ada', 'docs.example'));
    expect(c.popups.shown, isEmpty);
  });

  test('the pop-ups speak the page language', () {
    final alert = LiveAlert(
      category: LiveAlertCategory.accessRequest,
      event: _request('Jana', 'docs.google.com'),
      startsBurst: true,
      startsCategoryBurst: true,
    );
    expect(alertPopupText(_nl, alert, count: 3, names: true), (
      title: 'Nieuwe toegangsvragen (3)',
      body: 'Jana vraagt toegang tot docs.google.com',
    ));
  });
}
