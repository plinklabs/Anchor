import 'package:anchor_dashboard/alerts/live_alert_settings.dart';
import 'package:anchor_dashboard/alerts/live_alerts.dart';
import 'package:anchor_dashboard/realtime/session_hub_client.dart';
import 'package:flutter_test/flutter_test.dart';

// #454: which live-session events alert the teacher, how a burst of them is
// one alert, and the count of alerts they haven't seen.

SessionEvent _event(String kind, [Map<String, dynamic> payload = const {}]) =>
    SessionEvent(kind: kind, payload: payload, at: DateTime(2026, 6, 12, 9));

SessionEvent _request(String user, String host) =>
    _event('UnblockRequested', {'userId': user, 'host': host});

SessionEvent _state(String user, String state) =>
    _event('ParticipantStateChanged', {'userId': user, 'state': state});

SessionEvent _lost(String user) => _event('HeartbeatLost', {'userId': user});

SessionEvent _tamper(String user) =>
    _event('TamperDetected', {'userId': user, 'kind': 'inprivate_opened'});

SessionEvent _back(String user) => _event('AgentReconnected', {'userId': user});

class _Clock {
  DateTime now = DateTime(2026, 6, 12, 9);
  void advance(Duration by) => now = now.add(by);
}

({
  LiveAlerts alerts,
  _Clock clock,
  LiveAlertSettings settings,
  List<LiveAlert> seen,
})
_alerts() {
  final clock = _Clock();
  final settings = LiveAlertSettings(store: MemoryAlertSettingsStore());
  final alerts = LiveAlerts(settings: settings, clock: () => clock.now);
  final seen = <LiveAlert>[];
  alerts.alerts.listen(seen.add);
  addTearDown(alerts.dispose);
  return (alerts: alerts, clock: clock, settings: settings, seen: seen);
}

void main() {
  group('categories', () {
    test('requests, leaving, declining, a lost agent and tampering alert', () {
      expect(
        liveAlertCategoryOf(_request('ada', 'a.example')),
        LiveAlertCategory.accessRequest,
      );
      expect(
        liveAlertCategoryOf(_state('ada', 'Left')),
        LiveAlertCategory.studentLeft,
      );
      expect(
        liveAlertCategoryOf(_state('ada', 'Declined')),
        LiveAlertCategory.studentLeft,
      );
      expect(liveAlertCategoryOf(_lost('ada')), LiveAlertCategory.agentLost);
      expect(liveAlertCategoryOf(_tamper('ada')), LiveAlertCategory.tamper);
    });

    test('joining, an agent coming back and the session itself never do', () {
      for (final event in [
        _state('ada', 'Joined'),
        _state('ada', 'AgentMissing'),
        _back('ada'),
        _event('SessionStarted', {'sessionId': 's'}),
        _event('SessionEnded', {'sessionId': 's'}),
      ]) {
        expect(liveAlertCategoryOf(event), isNull, reason: event.kind);
      }
      final h = _alerts();
      h.alerts.handle(_state('ada', 'Joined'));
      h.alerts.handle(_back('ada'));
      expect(h.seen, isEmpty);
    });

    test('a category the teacher switched off raises nothing', () {
      final h = _alerts();
      h.settings.setOn(LiveAlertCategory.accessRequest, false);

      expect(h.alerts.handle(_request('ada', 'a.example')), isNull);
      expect(h.alerts.handle(_tamper('bo')), isNotNull);
      expect(h.seen.map((a) => a.category), [LiveAlertCategory.tamper]);
    });
  });

  group('bursts', () {
    test('twenty requests within a few seconds start one burst', () {
      final h = _alerts();
      for (var i = 0; i < 20; i++) {
        h.alerts.handle(_request('student$i', 'docs.example'));
        h.clock.advance(const Duration(milliseconds: 300));
      }

      expect(h.seen, hasLength(20));
      expect(h.seen.first.startsBurst, isTrue);
      expect(h.seen.skip(1).where((a) => a.startsBurst), isEmpty);
    });

    test('the first alert starts a burst at once, and one after a quiet '
        'window starts the next', () {
      final h = _alerts();
      expect(
        h.alerts.handle(_request('ada', 'a.example'))!.startsBurst,
        isTrue,
      );
      h.clock.advance(const Duration(seconds: 9));
      expect(
        h.alerts.handle(_request('bo', 'a.example'))!.startsBurst,
        isFalse,
      );
      // Ten seconds after the burst started, not after its last alert.
      h.clock.advance(const Duration(seconds: 1));
      expect(
        h.alerts.handle(_request('cas', 'a.example'))!.startsBurst,
        isTrue,
      );
    });

    test('a burst spans categories; each category keeps its own as well', () {
      final h = _alerts();
      final request = h.alerts.handle(_request('ada', 'a.example'))!;
      h.clock.advance(const Duration(seconds: 2));
      final left = h.alerts.handle(_state('bo', 'Left'))!;
      h.clock.advance(const Duration(seconds: 2));
      final another = h.alerts.handle(_request('cas', 'b.example'))!;

      expect(request.startsBurst, isTrue);
      expect(left.startsBurst, isFalse);
      expect(left.startsCategoryBurst, isTrue);
      expect(another.startsBurst, isFalse);
      expect(another.startsCategoryBurst, isFalse);
    });
  });

  group('one alert per episode', () {
    test('leaving and then a lost heartbeat for that student are one alert '
        '(#448)', () {
      final h = _alerts();
      expect(h.alerts.handle(_state('ada', 'Left')), isNotNull);
      // Twenty seconds later, past the burst window.
      h.clock.advance(const Duration(seconds: 20));
      expect(h.alerts.handle(_tamper('ada')), isNull);
      expect(h.alerts.handle(_lost('ada')), isNull);
      expect(h.alerts.handle(_state('ada', 'Declined')), isNull);
      expect(h.seen, hasLength(1));
    });

    test('a lost agent and then leaving are one alert', () {
      final h = _alerts();
      expect(h.alerts.handle(_lost('ada')), isNotNull);
      expect(h.alerts.handle(_state('ada', 'Left')), isNull);
      expect(h.seen, hasLength(1));
    });

    test('a student who joins again, or whose agent comes back, alerts '
        'again', () {
      final h = _alerts();
      h.alerts.handle(_state('ada', 'Left'));
      h.alerts.handle(_state('ada', 'Joined'));
      expect(h.alerts.handle(_state('ada', 'Left')), isNotNull);

      h.alerts.handle(_lost('bo'));
      h.alerts.handle(_back('bo'));
      expect(h.alerts.handle(_lost('bo')), isNotNull);
      expect(h.seen, hasLength(4));
    });

    test('other students are news of their own', () {
      final h = _alerts();
      h.alerts.handle(_state('ada', 'Left'));
      expect(h.alerts.handle(_lost('bo')), isNotNull);
      expect(h.alerts.handle(_tamper('cas')), isNotNull);
      expect(h.alerts.handle(_tamper('cas')), isNotNull);
    });

    test('the same student asking for the same site again is not new', () {
      final h = _alerts();
      expect(h.alerts.handle(_request('ada', 'a.example')), isNotNull);
      h.clock.advance(const Duration(minutes: 1));
      expect(h.alerts.handle(_request('ada', 'a.example')), isNull);
      expect(h.alerts.handle(_request('ada', 'b.example')), isNotNull);
      expect(h.alerts.handle(_request('bo', 'a.example')), isNotNull);
    });
  });

  group('unseen alerts', () {
    test('count only while the page is hidden, and clear when it shows', () {
      final h = _alerts();
      var notified = 0;
      h.alerts.addListener(() => notified++);

      h.alerts.handle(_request('ada', 'a.example'));
      expect(h.alerts.unseenCount, 0, reason: 'the teacher is looking');

      h.alerts.pageHidden = true;
      h.alerts.handle(_request('bo', 'a.example'));
      h.alerts.handle(_request('cas', 'a.example'));
      h.alerts.handle(_state('dina', 'Left'));
      // Not new: no count.
      h.alerts.handle(_lost('dina'));
      h.alerts.handle(_state('eli', 'Joined'));
      expect(h.alerts.unseenCount, 3);
      expect(h.alerts.unseenOf(LiveAlertCategory.accessRequest), 2);
      expect(h.alerts.unseenOf(LiveAlertCategory.studentLeft), 1);
      expect(notified, 3);

      h.alerts.pageHidden = false;
      expect(h.alerts.unseenCount, 0);
      expect(h.alerts.unseenOf(LiveAlertCategory.accessRequest), 0);
      expect(notified, 4);
    });
  });
}
