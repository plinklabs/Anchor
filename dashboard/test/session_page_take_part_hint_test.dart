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

// #410: a teacher isn't held to the session they start. Taking part is their
// choice, with the join code the live page shows, from the agent's "join by
// code" dialog. The page says so under the code, in the teacher's language,
// for as long as the code shows.

const _sessionId = 'aaaaaaaa-0000-0000-0000-000000000001';
const _code = '482913';

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

  DateTime? endedAt;

  @override
  Future<SessionDetail> getSession(String sessionId) async => SessionDetail(
    id: sessionId,
    classId: 'c1',
    className: '3A',
    joinCode: _code,
    startedAt: DateTime(2026, 5, 26, 9, 15),
    endedAt: endedAt,
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

Future<({_StubHub hub, _FakeSessions sessions})> _pumpLiveSession(
  WidgetTester tester,
  Locale locale,
) async {
  final hub = _StubHub();
  final sessions = _FakeSessions();
  await tester.pumpWidget(
    MaterialApp(
      locale: locale,
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
  return (hub: hub, sessions: sessions);
}

void main() {
  for (final (locale, label, hint) in const [
    (
      Locale('en'),
      'Join code',
      "Taking part yourself? Use 'Join by code' in the agent.",
    ),
    (
      Locale('nl'),
      'Toegangscode',
      "Zelf meedoen? Gebruik 'Deelnemen met code' in de agent.",
    ),
  ]) {
    testWidgets('the join code says how the teacher takes part themselves '
        '(${locale.languageCode})', (tester) async {
      await _pumpLiveSession(tester, locale);

      expect(find.text(label), findsOneWidget);
      expect(find.text(_code), findsOneWidget);
      expect(find.text(hint), findsOneWidget);
      // Under the code it is about, in the same panel.
      expect(
        tester.getTopLeft(find.text(hint)).dy,
        greaterThan(tester.getBottomLeft(find.text(_code)).dy),
      );
      expect(
        tester.getTopLeft(find.text(hint)).dx,
        tester.getTopLeft(find.text(_code)).dx,
      );
    });
  }

  testWidgets('the hint goes with the code once the session ends', (
    tester,
  ) async {
    final page = await _pumpLiveSession(tester, const Locale('en'));
    const hint = "Taking part yourself? Use 'Join by code' in the agent.";
    expect(find.text(hint), findsOneWidget);

    page.sessions.endedAt = DateTime(2026, 5, 26, 10, 15);
    page.hub.emit('SessionEnded', {'sessionId': _sessionId});
    await tester.pumpAndSettle();

    expect(find.text(_code), findsNothing);
    expect(find.text(hint), findsNothing);
  });
}
