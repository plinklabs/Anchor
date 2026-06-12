import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/auth_token_store.dart';
import 'package:anchor_dashboard/api/bundles_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/pages/session_page.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

// The dashboard authenticates via MSAL and can't be dev-impersonated, so we
// pump the page with fake API subclasses rather than hitting a real backend
// (see reference_dashboard_widget_test_verify). Covers the #101 approve-scope
// choice on a pending unblock-request row: the primary button is per-student,
// the kebab offers whole-class.
ApiClient _dummyClient() => ApiClient(
  baseUrl: Uri.parse('http://localhost'),
  tokenProvider: () async => null,
);

final _now = DateTime(2026, 6, 12, 9, 20);

UnblockRequestSummary _pending() => UnblockRequestSummary(
  host: 'chat.example.com',
  count: 2,
  firstRequestedAt: _now,
  latestRequestedAt: _now,
  requesters: [
    UnblockRequestRequester(userId: 'u1', displayName: 'Ada', requestedAt: _now),
    UnblockRequestRequester(userId: 'u2', displayName: 'Bo', requestedAt: _now),
  ],
);

class _FakeSessions extends SessionsApi {
  _FakeSessions() : super(_dummyClient());

  List<UnblockRequestSummary> pending = [_pending()];

  // (userId, host) per per-student approval call.
  final List<(String, String)> perStudentCalls = [];
  // host per whole-class approval call.
  final List<String> classCalls = [];

  @override
  Future<SessionDetail> getSession(String sessionId) async => SessionDetail(
    id: sessionId,
    classId: 'c1',
    className: 'Class',
    joinCode: '',
    startedAt: DateTime(2026, 6, 12, 9, 15),
    endedAt: null,
    summaries: const [],
    recentEvents: const [],
    participants: const [],
    bundles: const [],
    grants: const [],
  );

  @override
  Future<List<UnblockRequestSummary>> unblockRequests(String sessionId) async =>
      pending;

  @override
  Future<void> approveUnblock(String sessionId, String userId, String host) async {
    perStudentCalls.add((userId, host));
  }

  @override
  Future<void> approveUnblockForClass(String sessionId, String host) async {
    classCalls.add(host);
  }
}

class _FakeBundles extends BundlesApi {
  _FakeBundles() : super(_dummyClient());

  @override
  Future<List<BundleSummary>> list({bool includeArchived = false}) async => const [];
}

Future<void> _pumpPage(WidgetTester tester, _FakeSessions sessions) async {
  await tester.pumpWidget(
    MaterialApp(
      // NoSplash avoids the ink_sparkle fragment shader, which fails to decode
      // under the test engine when a tap triggers a ripple.
      theme: ThemeData(splashFactory: NoSplash.splashFactory),
      home: SessionPage(
        sessionId: '11111111-2222-3333-4444-555555555555',
        tokens: AuthTokenStore(),
        sessions: sessions,
        bundles: _FakeBundles(),
        apiBaseUrl: Uri.parse('http://localhost'),
      ),
    ),
  );
  // Settle initState's getSession + unblockRequests + bundles.list microtasks.
  // Avoid pumpAndSettle: the real hub client keeps retrying localhost and stalls.
  await tester.pump();
  await tester.pump();
}

void main() {
  testWidgets('primary Approve grants per requesting student (#101)', (tester) async {
    final sessions = _FakeSessions();
    await _pumpPage(tester, sessions);

    expect(find.text('chat.example.com'), findsOneWidget);
    final approve = find.widgetWithText(FilledButton, 'Approve');
    expect(approve, findsOneWidget);

    await tester.tap(approve);
    await tester.pump();
    await tester.pump();

    // One grant per requester, and the whole-class path was untouched.
    expect(sessions.perStudentCalls, [
      ('u1', 'chat.example.com'),
      ('u2', 'chat.example.com'),
    ]);
    expect(sessions.classCalls, isEmpty);
  });

  testWidgets('kebab "Approve for whole class" grants session-wide (#101)', (
    tester,
  ) async {
    final sessions = _FakeSessions();
    await _pumpPage(tester, sessions);

    // Open the overflow menu and pick the whole-class option.
    await tester.tap(find.byIcon(Icons.more_vert));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 300));

    expect(find.text('Approve for whole class'), findsOneWidget);
    await tester.tap(find.text('Approve for whole class'));
    await tester.pump();
    await tester.pump();

    // Exactly one whole-class POST for the host; no per-student grants issued.
    expect(sessions.classCalls, ['chat.example.com']);
    expect(sessions.perStudentCalls, isEmpty);
  });
}
