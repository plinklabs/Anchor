import 'package:anchor_dashboard/feed/live_feed.dart';
import 'package:anchor_dashboard/l10n/app_localizations.dart';
import 'package:anchor_dashboard/realtime/session_hub_client.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';

// #491: the live session's activity feed reads as plain sentences, and by
// default shows only what needs the teacher's attention. These pin the
// mapping from hub events to sentences and priorities; the page that shows
// them is in session_page_feed_test.dart, the real app in
// integration_test/live_session_test.dart.

const _sessionId = '5fbae478-1111-2222-3333-444444444444';
const _adaId = 'edee9562-1111-2222-3333-444444444444';
const _boId = '0a1b2c3d-1111-2222-3333-444444444444';

final AppLocalizations en = lookupAppLocalizations(const Locale('en'));
final AppLocalizations nl = lookupAppLocalizations(const Locale('nl'));

SessionEvent _event(String kind, [Map<String, dynamic> payload = const {}]) =>
    SessionEvent(
      kind: kind,
      payload: {'sessionId': _sessionId, ...payload},
      at: DateTime.utc(2026, 11, 11, 19, 28, 11),
    );

SessionEvent _state(String state, {String id = _adaId, String name = 'Ada'}) =>
    _event('ParticipantStateChanged', {
      'userId': id,
      'displayName': name,
      'state': state,
      'at': '2026-11-11T19:28:11.3797761+00:00',
    });

SessionEvent _tamper(String kind) => _event('TamperDetected', {
  'userId': _adaId,
  'userDisplayName': 'Ada',
  'kind': kind,
  'detectedAt': '2026-11-11T19:28:11+00:00',
});

String? _roster(String userId) => const {_adaId: 'Ada', _boId: 'Bo'}[userId];

void main() {
  group('a sentence per event', () {
    test('a session starting says its code and what it allows', () {
      final started = _event('SessionStarted', {
        'classId': 'c1',
        'startedAt': '2026-11-11T19:25:44+00:00',
        'joinCode': '019480',
        'apps': [
          {'matchKind': 'ProcessName', 'value': 'msedge'},
          {'matchKind': 'ProcessName', 'value': 'code'},
        ],
        'domains': [
          for (var i = 0; i < 10; i++)
            {'matchType': 'Wildcard', 'value': 'site$i.example'},
        ],
      });
      expect(
        feedSentence(en, started),
        'Session started, code 019480: 2 apps and 10 websites allowed.',
      );
      expect(
        feedSentence(nl, started),
        'Sessie gestart, code 019480: 2 apps en 10 websites toegelaten.',
      );

      final bare = _event('SessionStarted', {
        'joinCode': '019480',
        'apps': [
          {'matchKind': 'ProcessName', 'value': 'msedge'},
        ],
        'domains': const [],
      });
      expect(
        feedSentence(en, bare),
        'Session started, code 019480: 1 app and no websites allowed.',
      );
      expect(
        feedSentence(nl, bare),
        'Sessie gestart, code 019480: 1 app en geen websites toegelaten.',
      );

      // As far as the payload says, never more.
      expect(
        feedSentence(en, _event('SessionStarted', {'joinCode': '019480'})),
        'Session started, code 019480.',
      );
      expect(feedSentence(en, _event('SessionStarted')), 'Session started.');
    });

    test('a session ending', () {
      expect(feedSentence(en, _event('SessionEnded')), 'Session ended.');
      expect(feedSentence(nl, _event('SessionEnded')), 'Sessie beëindigd.');
    });

    test('a student changing state, by state', () {
      const expected = {
        'Joined': ('Ada joined.', 'Ada is aangesloten.'),
        'Left': ('Ada left the session.', 'Ada heeft de sessie verlaten.'),
        'Declined': (
          'Ada declined the session.',
          'Ada heeft de sessie geweigerd.',
        ),
        'AgentMissing': (
          "Ada's agent hasn't reported in.",
          'De agent van Ada heeft zich niet gemeld.',
        ),
        'HeartbeatStale': (
          "Ada's agent stopped reporting.",
          'De agent van Ada rapporteert niet meer.',
        ),
        'NeverJoined': (
          "Ada hasn't joined yet.",
          'Ada heeft zich nog niet aangesloten.',
        ),
        'SomethingNew': (
          "Ada's status changed.",
          'De status van Ada is gewijzigd.',
        ),
      };
      for (final MapEntry(key: state, value: (english, dutch))
          in expected.entries) {
        expect(feedSentence(en, _state(state)), english, reason: state);
        expect(feedSentence(nl, _state(state)), dutch, reason: state);
      }
    });

    test('an agent that stops reporting or comes back, named from the '
        'roster', () {
      final lost = _event('HeartbeatLost', {
        'userId': _boId,
        'lastSeenAt': '2026-11-11T19:28:11+00:00',
      });
      final back = _event('AgentReconnected', {
        'userId': _boId,
        'reconnectedAt': '2026-11-11T19:29:11+00:00',
      });
      expect(
        feedSentence(en, lost, nameOf: _roster),
        "Bo's agent stopped reporting.",
      );
      expect(
        feedSentence(nl, lost, nameOf: _roster),
        'De agent van Bo rapporteert niet meer.',
      );
      expect(feedSentence(en, back, nameOf: _roster), 'Bo is connected again.');
      expect(feedSentence(nl, back, nameOf: _roster), 'Bo is weer verbonden.');

      // Not on the roster: still a sentence, never the id.
      expect(
        feedSentence(en, lost, nameOf: (_) => null),
        "A student's agent stopped reporting.",
      );
      expect(
        feedSentence(nl, lost, nameOf: (_) => null),
        'De agent van een leerling rapporteert niet meer.',
      );
    });

    test('an access request names the student and the site', () {
      final asks = _event('UnblockRequested', {
        'userId': _adaId,
        'userDisplayName': 'Ada',
        'host': 'example.com',
        'url': 'https://example.com/page',
        'requestedAt': '2026-11-11T19:28:11+00:00',
      });
      expect(feedSentence(en, asks), 'Ada asks for access to example.com.');
      expect(feedSentence(nl, asks), 'Ada vraagt toegang tot example.com.');
      expect(
        feedSentence(en, _event('UnblockRequested', {'userId': _adaId})),
        'A student asks for access to a website.',
      );
    });

    test('a tamper report, by kind', () {
      const expected = {
        'extension_disabled': (
          "Ada's Anchor extension was switched off or removed.",
          'De Anchor-extensie van Ada is uitgeschakeld of verwijderd.',
        ),
        'extension_silent': (
          "Ada's Anchor extension is off or not responding.",
          'De Anchor-extensie van Ada staat uit of reageert niet.',
        ),
        'inprivate_opened': (
          'Ada opened an InPrivate window.',
          'Ada heeft een InPrivate-venster geopend.',
        ),
        'host_permission_revoked': (
          "Ada limited the Anchor extension's access to websites.",
          'Ada heeft de toegang van de Anchor-extensie tot websites beperkt.',
        ),
        'agent_unavailable': (
          "Ada's Anchor app stopped running.",
          'De Anchor-app van Ada draait niet meer.',
        ),
        // A kind this dashboard doesn't know yet: a sentence, not the code.
        'clock_tampered': (
          "Tampering detected on Ada's computer.",
          'Manipulatie gedetecteerd op de computer van Ada.',
        ),
      };
      for (final MapEntry(key: kind, value: (english, dutch))
          in expected.entries) {
        expect(feedSentence(en, _tamper(kind)), english, reason: kind);
        expect(feedSentence(nl, _tamper(kind)), dutch, reason: kind);
      }
    });

    test('an event the feed does not know gets a sentence, not a dump', () {
      final future = _event('SomethingNew', {'userId': _adaId, 'x': 1});
      expect(feedSentence(en, future), 'Something happened in the session.');
      expect(feedSentence(nl, future), 'Er is iets gebeurd in de sessie.');
    });

    test('no sentence carries an id, an enum name or an ISO timestamp', () {
      final events = [
        _event('SessionStarted', {
          'joinCode': '019480',
          'startedAt': '2026-11-11T19:25:44+00:00',
          'apps': const [],
          'domains': const [],
        }),
        _event('SessionEnded'),
        for (final s in ['Joined', 'Left', 'Declined', 'AgentMissing', 'X'])
          _state(s),
        _event('HeartbeatLost', {'userId': _adaId}),
        _event('AgentReconnected', {'userId': _adaId}),
        _event('UnblockRequested', {
          'userId': _adaId,
          'userDisplayName': 'Ada',
          'host': 'example.com',
        }),
        _tamper('extension_silent'),
        _event('SomethingNew'),
      ];
      for (final e in events) {
        for (final l10n in [en, nl]) {
          final sentence = feedSentence(l10n, e, nameOf: _roster);
          expect(sentence, isNot(contains(_sessionId)), reason: e.kind);
          expect(sentence, isNot(contains(_adaId)), reason: e.kind);
          expect(sentence, isNot(contains('+00:00')), reason: e.kind);
          expect(sentence, isNot(contains(e.kind)), reason: e.kind);
          expect(sentence, isNot(contains('=')), reason: e.kind);
        }
      }
    });
  });

  group('what needs the teacher', () {
    test('follows the live alert categories, and adds the states that '
        'leave a laptop unenforced', () {
      final expected = {
        _event('UnblockRequested', {'userId': _adaId}): FeedPriority.attention,
        _state('Left'): FeedPriority.attention,
        _state('Declined'): FeedPriority.attention,
        _event('HeartbeatLost', {'userId': _adaId}): FeedPriority.warning,
        _tamper('extension_silent'): FeedPriority.warning,
        _state('AgentMissing'): FeedPriority.warning,
        _state('HeartbeatStale'): FeedPriority.warning,
        _state('Joined'): FeedPriority.routine,
        _state('NeverJoined'): FeedPriority.routine,
        _event('AgentReconnected', {'userId': _adaId}): FeedPriority.routine,
        _event('SessionStarted', {'joinCode': '1'}): FeedPriority.routine,
        _event('SessionEnded'): FeedPriority.routine,
        _event('SomethingNew'): FeedPriority.routine,
      };
      for (final MapEntry(key: event, value: priority) in expected.entries) {
        expect(
          feedPriorityOf(event),
          priority,
          reason: '${event.kind} ${event.payload['state'] ?? ''}',
        );
      }
    });

    test('with "show all" off only what needs attention is a row; on, '
        'everything, newest first as it came', () {
      // Newest first, as the page keeps them.
      final events = [
        _event('HeartbeatLost', {'userId': _boId}),
        _state('Joined', id: _boId, name: 'Bo'),
        _state('Left'),
        _state('Joined'),
        _event('SessionStarted', {'joinCode': '019480'}),
      ];

      final attention = feedEntries(
        en,
        events,
        showAll: false,
        nameOf: _roster,
      );
      expect(attention.map((e) => e.sentence), [
        "Bo's agent stopped reporting.",
        'Ada left the session.',
      ]);
      expect(attention.map((e) => e.priority), [
        FeedPriority.warning,
        FeedPriority.attention,
      ]);
      expect(attention.first.event, same(events.first));

      final all = feedEntries(en, events, showAll: true, nameOf: _roster);
      expect(all.map((e) => e.sentence), [
        "Bo's agent stopped reporting.",
        'Bo joined.',
        'Ada left the session.',
        'Ada joined.',
        'Session started, code 019480.',
      ]);

      // Joins only: nothing to show by default.
      expect(
        feedEntries(en, [_state('Joined')], showAll: false, nameOf: _roster),
        isEmpty,
      );
    });

    test('the same sentence about the same student in a row is one row, '
        'with its count, the newest time', () {
      // A student who reconnects can send several in a row; with joins
      // hidden, leaving twice reads as a run too.
      final newest = _state('Left');
      final events = [
        newest,
        _state('Joined'),
        _state('Left'),
        _state('Left'),
        _state('Left', id: _boId, name: 'Bo'),
        // Another student of the same name is another row.
        _state('Left', id: 'other-ada'),
      ];

      final shown = feedEntries(en, events, showAll: false);
      expect(shown.map((e) => (e.sentence, e.count)), [
        ('Ada left the session.', 3),
        ('Bo left the session.', 1),
        ('Ada left the session.', 1),
      ]);
      expect(shown.first.event, same(newest));

      // Shown with the joins between them, they're no run.
      final all = feedEntries(en, events, showAll: true);
      expect(all.map((e) => (e.sentence, e.count)), [
        ('Ada left the session.', 1),
        ('Ada joined.', 1),
        ('Ada left the session.', 2),
        ('Bo left the session.', 1),
        ('Ada left the session.', 1),
      ]);
    });
  });
}
