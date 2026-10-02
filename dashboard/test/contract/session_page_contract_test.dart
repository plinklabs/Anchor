import 'dart:async';
import 'dart:collection';

import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/auth_token_store.dart';
import 'package:anchor_dashboard/api/bundles_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/l10n/app_localizations.dart';
import 'package:anchor_dashboard/pages/session_page.dart';
import 'package:anchor_dashboard/realtime/session_hub_client.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import '../support/session_hub_contract.dart';

// #390: the live page's side of contracts/session-hub.json. Every event the
// contract lists has to reach the teacher's live page, which may read only the
// payload fields the contract gives it. An event the backend sends to the
// teacher's user group reaches every connection of theirs, whatever session
// its page shows, so the page has to ignore one that names another session
// (233a64f). That is how #354 broke the page: it moved SessionEnded from the
// session group to user groups, and the page put the teacher's other
// sessions' ends in its feed. With the contract, that move fails Backend CI
// until the file says so, and the file's change runs these tests.

const _sessionId = 'aaaaaaaa-0000-0000-0000-000000000001';
const _otherSessionId = 'bbbbbbbb-0000-0000-0000-000000000002';

ApiClient _dummyClient() => ApiClient(
  baseUrl: Uri.parse('http://localhost'),
  tokenProvider: () async => null,
);

class _StubHub extends SessionHubClient {
  _StubHub()
    : super(apiBaseUrl: Uri.parse('http://localhost'), tokenProvider: _noToken);

  static Future<String?> _noToken() async => null;

  final _ctrl = StreamController<SessionEvent>.broadcast();
  final joined = <String>[];

  @override
  Stream<SessionEvent> get events => _ctrl.stream;

  @override
  Future<void> connect() async {}
  @override
  Future<void> joinSession(String sessionId, {String? joinCode}) async =>
      joined.add(sessionId);
  @override
  Future<void> leaveSession(String sessionId) async {}
  @override
  Future<void> disconnect() async {}
  @override
  Future<void> dispose() async => _ctrl.close();
}

/// Counts the page's loads, which an event about its session can trigger.
class _FakeSessions extends SessionsApi {
  _FakeSessions() : super(_dummyClient());

  int loads = 0;

  @override
  Future<SessionDetail> getSession(String sessionId) async {
    loads++;
    return SessionDetail(
      id: sessionId,
      classId: 'c1',
      className: 'Class',
      joinCode: 'ABC123',
      startedAt: DateTime(2026, 5, 26, 9, 15),
      endedAt: null,
      summaries: const [],
      recentEvents: const [],
      participants: const [],
      bundles: const [],
      grants: const [],
    );
  }

  @override
  Future<List<UnblockRequestSummary>> unblockRequests(String sessionId) async {
    loads++;
    return const [];
  }
}

class _FakeBundles extends BundlesApi {
  _FakeBundles() : super(_dummyClient());

  @override
  Future<List<BundleSummary>> list({bool includeArchived = false}) async =>
      const [];
}

/// A payload as the hub client passes it on, which records every field the
/// page looks up in it. Showing the whole payload in the feed looks none up.
class _Payload extends MapBase<String, dynamic> {
  _Payload(this._fields);

  final Map<String, dynamic> _fields;
  final lookedUp = <Object?>{};

  @override
  dynamic operator [](Object? key) {
    lookedUp.add(key);
    return _fields[key];
  }

  @override
  bool containsKey(Object? key) {
    lookedUp.add(key);
    return _fields.containsKey(key);
  }

  @override
  void operator []=(String key, dynamic value) => _fields[key] = value;

  @override
  Iterable<String> get keys => _fields.keys;

  @override
  Iterable<MapEntry<String, dynamic>> get entries => _fields.entries;

  @override
  dynamic remove(Object? key) => _fields.remove(key);

  @override
  void clear() => _fields.clear();
}

class _LivePage {
  _LivePage(this.hub, this.sessions);

  final _StubHub hub;
  final _FakeSessions sessions;

  /// Pushes [event] about [sessionId], with the payload the contract gives
  /// it, as the hub client passes it on.
  _Payload push(HubEvent event, String sessionId) {
    final payload = _Payload(event.payload(sessionId));
    hub._ctrl.add(
      SessionEvent(kind: event.name, payload: payload, at: DateTime.now()),
    );
    return payload;
  }
}

Future<_LivePage> _pumpLiveSession(WidgetTester tester) async {
  final hub = _StubHub();
  final sessions = _FakeSessions();
  await tester.pumpWidget(
    MaterialApp(
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      home: SessionPage(
        sessionId: _sessionId,
        tokens: AuthTokenStore(),
        sessions: sessions,
        bundles: _FakeBundles(),
        apiBaseUrl: Uri.parse('http://localhost'),
        hubClientFactory: ({required apiBaseUrl, required tokenProvider}) =>
            hub,
      ),
    ),
  );
  await tester.pumpAndSettle();
  return _LivePage(hub, sessions);
}

void _expectReadsOnlyItsFields(HubEvent event, _Payload payload) {
  expect(
    payload.lookedUp,
    everyElement(isIn(event.names)),
    reason:
        'The live page looks up ${payload.lookedUp} in ${event.name}, '
        'which carries ${event.names} per the contract.',
  );
}

void main() {
  final contract = SessionHubContract.load();

  test("every event in the contract reaches the teacher's live page", () {
    for (final event in contract.events) {
      expect(
        event.reachesTheSessionGroup || event.reachesTheTeachersUserGroup,
        isTrue,
        reason:
            '${event.name} goes to ${event.audience}: neither the session '
            "group the page joins nor the teacher's user group.",
      );
    }
  });

  for (final event in contract.events) {
    final audience = event.audience.join(', ');

    testWidgets('${event.name} about this session lands in its feed '
        '(sent to: $audience)', (tester) async {
      final page = await _pumpLiveSession(tester);
      if (!event.reachesTheTeachersUserGroup) {
        // Only through the session group, which the page has to join.
        expect(page.hub.joined, contains(_sessionId));
      }

      final payload = page.push(event, _sessionId);
      await tester.pumpAndSettle();

      expect(find.text(event.name), findsOneWidget);
      _expectReadsOnlyItsFields(event, payload);
    });

    if (event.reachesTheTeachersUserGroup) {
      testWidgets('${event.name} naming another session leaves the page as '
          "it was (sent to: $audience, the teacher's user group hears about "
          'all their sessions)', (tester) async {
        final page = await _pumpLiveSession(tester);
        final loads = page.sessions.loads;

        final payload = page.push(event, _otherSessionId);
        await tester.pumpAndSettle();

        expect(find.text(event.name), findsNothing);
        expect(find.text('Waiting for events…'), findsOneWidget);
        expect(find.text('LIVE'), findsOneWidget);
        expect(
          find.text('Session ended — event stream stopped.'),
          findsNothing,
        );
        expect(page.sessions.loads, loads, reason: 'nothing to load again');
        _expectReadsOnlyItsFields(event, payload);
      });
    }
  }
}
