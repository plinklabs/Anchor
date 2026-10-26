import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/auth_token_store.dart';
import 'package:anchor_dashboard/api/bundles_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/l10n/app_localizations.dart';
import 'package:anchor_dashboard/pages/session_page.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

ApiClient _dummyClient() => ApiClient(
  baseUrl: Uri.parse('http://localhost'),
  tokenProvider: () async => null,
);

class _FakeBundles extends BundlesApi {
  _FakeBundles() : super(_dummyClient());

  @override
  Future<List<BundleSummary>> list({bool includeArchived = false}) async =>
      const [];
}

class _FakeSessions extends SessionsApi {
  _FakeSessions({required this.startedAt, this.endsAt}) : super(_dummyClient());

  final DateTime startedAt;
  final DateTime? endsAt;

  @override
  Future<SessionDetail> getSession(String sessionId) async => SessionDetail(
    id: sessionId,
    classId: 'c1',
    className: 'Class',
    joinCode: '',
    startedAt: startedAt,
    endedAt: null,
    endsAt: endsAt,
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

Widget _page(_FakeSessions sessions) => MaterialApp(
  localizationsDelegates: AppLocalizations.localizationsDelegates,
  supportedLocales: AppLocalizations.supportedLocales,
  home: SessionPage(
    sessionId: '11111111-2222-3333-4444-555555555555',
    tokens: AuthTokenStore(),
    sessions: sessions,
    bundles: _FakeBundles(),
    apiBaseUrl: Uri.parse('http://localhost'),
  ),
);

void main() {
  testWidgets('the header says when a live session is planned to end (#447)', (
    tester,
  ) async {
    await tester.pumpWidget(
      _page(
        _FakeSessions(
          startedAt: DateTime(2026, 10, 26, 9, 15),
          endsAt: DateTime(2026, 10, 26, 10, 5),
        ),
      ),
    );
    // As below: no pumpAndSettle, the real hub client never settles.
    await tester.pump();
    await tester.pump();

    expect(find.text('Session 2026-10-26 09:15 · ends 10:05'), findsOneWidget);
    // The teacher can still end it earlier.
    expect(find.text('End session'), findsOneWidget);
  });

  testWidgets(
    'AppBar title shows session start date/time, not the GUID (#99)',
    (tester) async {
      final started = DateTime(2026, 5, 26, 9, 15);

      await tester.pumpWidget(
        MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: SessionPage(
            sessionId: '11111111-2222-3333-4444-555555555555',
            tokens: AuthTokenStore(),
            sessions: _FakeSessions(startedAt: started),
            bundles: _FakeBundles(),
            apiBaseUrl: Uri.parse('http://localhost'),
          ),
        ),
      );
      // Two pumps: first flushes initState's microtasks (getSession resolves
      // synchronously in the fake), second processes the setState that stores
      // the detail. We deliberately avoid pumpAndSettle: the real hub client
      // would otherwise keep trying to reach localhost and stall the test.
      await tester.pump();
      await tester.pump();

      expect(find.text('Session 2026-05-26 09:15'), findsOneWidget);
      expect(
        find.text('Session 11111111-2222-3333-4444-555555555555'),
        findsNothing,
      );
    },
  );
}
