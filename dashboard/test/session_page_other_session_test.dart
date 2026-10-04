import 'dart:async';

import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/auth_token_store.dart';
import 'package:anchor_dashboard/api/bundles_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/l10n/app_localizations.dart';
import 'package:anchor_dashboard/pages/session_page.dart';
import 'package:anchor_dashboard/realtime/session_hub_client.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

// #354: the backend sends SessionEnded to the teacher's user group for every
// session they own, and SessionStarted to it for every session of their
// classes, so the live page's connection also hears about the teacher's other
// sessions. The page shows one session: those events must neither end it nor
// show up in its feed.

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

  @override
  Stream<SessionEvent> get events => _ctrl.stream;

  void emit(String kind, Map<String, dynamic> payload) =>
      _ctrl.add(SessionEvent(kind: kind, payload: payload, at: DateTime.now()));

  @override
  Future<void> connect() async {}
  @override
  Future<void> joinSession(String sessionId, {String? joinCode}) async {}
  @override
  Future<void> leaveSession(String sessionId) async {}
  @override
  Future<void> disconnect() async {}
  @override
  Future<void> dispose() async => _ctrl.close();
}

class _FakeSessions extends SessionsApi {
  _FakeSessions() : super(_dummyClient());

  @override
  Future<SessionDetail> getSession(String sessionId) async => SessionDetail(
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

  @override
  Future<List<UnblockRequestSummary>> unblockRequests(String sessionId) async =>
      const [];
}

class _FakeBundles extends BundlesApi {
  _FakeBundles() : super(_dummyClient());

  @override
  Future<List<BundleSummary>> list({bool includeArchived = false}) async =>
      const [];
}

Future<_StubHub> _pumpLiveSession(WidgetTester tester) async {
  final hub = _StubHub();
  await tester.pumpWidget(
    MaterialApp(
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      home: SessionPage(
        sessionId: _sessionId,
        tokens: AuthTokenStore(),
        sessions: _FakeSessions(),
        bundles: _FakeBundles(),
        apiBaseUrl: Uri.parse('http://localhost'),
        hubClientFactory: ({required apiBaseUrl, required tokenProvider}) =>
            hub,
      ),
    ),
  );
  await tester.pumpAndSettle();
  return hub;
}

void main() {
  testWidgets(
    "another session ending neither ends this live page nor lands in its feed (#354)",
    (tester) async {
      final hub = await _pumpLiveSession(tester);

      hub.emit('SessionEnded', {'sessionId': _otherSessionId});
      await tester.pumpAndSettle();

      expect(find.text('Session ended — event stream stopped.'), findsNothing);
      expect(find.text('LIVE'), findsOneWidget);
      expect(find.text('SessionEnded'), findsNothing);
      expect(find.text('Waiting for events…'), findsOneWidget);

      // Its own end still ends it.
      hub.emit('SessionEnded', {'sessionId': _sessionId});
      await tester.pumpAndSettle();

      expect(
        find.text('Session ended — event stream stopped.'),
        findsOneWidget,
      );
      expect(find.text('LIVE'), findsNothing);
      expect(find.text('SessionEnded'), findsOneWidget);
    },
  );

  testWidgets("another session starting doesn't land in this page's feed", (
    tester,
  ) async {
    final hub = await _pumpLiveSession(tester);

    hub.emit('SessionStarted', {'sessionId': _otherSessionId});
    await tester.pumpAndSettle();

    expect(find.text('SessionStarted'), findsNothing);
    expect(find.text('Waiting for events…'), findsOneWidget);
  });
}
