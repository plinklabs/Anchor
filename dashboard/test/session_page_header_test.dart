import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/auth_token_store.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/pages/session_page.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

ApiClient _dummyClient() => ApiClient(
  baseUrl: Uri.parse('http://localhost'),
  tokenProvider: () async => null,
);

class _FakeSessions extends SessionsApi {
  _FakeSessions({required this.startedAt}) : super(_dummyClient());

  final DateTime startedAt;

  @override
  Future<SessionDetail> getSession(String sessionId) async => SessionDetail(
    id: sessionId,
    classId: 'c1',
    className: 'Class',
    joinCode: '',
    startedAt: startedAt,
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

void main() {
  testWidgets('AppBar title shows session start date/time, not the GUID (#99)',
      (tester) async {
    final started = DateTime(2026, 5, 26, 9, 15);

    await tester.pumpWidget(
      MaterialApp(
        home: SessionPage(
          sessionId: '11111111-2222-3333-4444-555555555555',
          tokens: AuthTokenStore(),
          sessions: _FakeSessions(startedAt: started),
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
  });
}
