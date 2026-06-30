import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/auth_token_store.dart';
import 'package:anchor_dashboard/api/bundles_api.dart';
import 'package:anchor_dashboard/api/classes_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/auth/msal_auth_service.dart';
import 'package:anchor_dashboard/main.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';
import 'package:plink_design_system/plink_design_system.dart';

// Real-app e2e for the teacher dashboard's history archive (AD7, #172).
//
// Like the live-session e2e (#132), a real-backend run is infeasible here (MSAL
// auth + a real bearer token), so this boots the *real* AnchorDashboard app
// (real router, real navigation, real layout, and — under `flutter drive` — the
// real Fraunces / Space Mono fonts) wired to fake API subclasses, then drives
// the real flow the user takes: Home → History nav → open a past session.
//
// This is the test that catches what the isolated widget tests structurally
// miss: a real-font overflow, a broken composition under the shell chrome, and
// the actual /history → /history/:id navigation. The archive must read in the
// muted register — a calm "Ended" outline badge, never the magenta LIVE spark.

final _startedAt = DateTime(2026, 6, 12, 9, 15);
final _endedAt = DateTime(2026, 6, 12, 10, 5);
const _sessionId = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee';

ApiClient _dummyClient() => ApiClient(
  baseUrl: Uri.parse('http://localhost'),
  tokenProvider: () async => null,
);

/// Past the /login redirect without MSAL — the router only checks
/// `tokens.isAuthenticated`, and nothing in the faked flow calls the auth
/// service, so a no-op implementation is enough.
class _FakeAuth implements MsalAuthService {
  @override
  Future<void> initialize() async {}
  @override
  Future<AccountInfo?> signIn() async => null;
  @override
  Future<void> signOut() async {}
  @override
  Future<String> acquireToken() async => 'fake-token';
  @override
  Future<String> acquireTokenSilent() async => 'fake-token';
  @override
  AccountInfo? currentAccount() => null;
}

class _FakeSessions extends SessionsApi {
  _FakeSessions() : super(_dummyClient());

  @override
  Future<MeResponse> me() async =>
      MeResponse(id: 't1', displayName: 'Teacher', role: 'Teacher');

  @override
  Future<List<ClassSummary>> classes() async => [
    ClassSummary(id: 'c1', name: 'Math 101', schoolYear: '2025-2026'),
  ];

  @override
  Future<List<ActiveSession>> activeSessions() async => const [];

  @override
  Future<List<SessionHistoryEntry>> history({
    int limit = 50,
    int offset = 0,
  }) async {
    if (offset > 0) return const <SessionHistoryEntry>[];
    return [
      SessionHistoryEntry(
        id: _sessionId,
        classId: 'c1',
        className: 'Math 101',
        startedAt: _startedAt,
        endedAt: _endedAt,
      ),
    ];
  }

  @override
  Future<SessionDetail> getSession(String sessionId) async => SessionDetail(
    id: sessionId,
    classId: 'c1',
    className: 'Math 101',
    joinCode: 'ABC123',
    startedAt: _startedAt,
    endedAt: _endedAt,
    summaries: [
      SessionEventSummary(
        userId: 'u1',
        kind: 'ForegroundChange',
        count: 12,
        firstAt: _startedAt,
        lastAt: _endedAt,
      ),
    ],
    recentEvents: [
      SessionRecentEvent(
        id: 'e1',
        userId: 'u1',
        kind: 'BlockedUrl',
        payloadJson: '{"host":"chat.example.com"}',
        occurredAt: DateTime(2026, 6, 12, 9, 30, 5),
      ),
    ],
    participants: [
      SessionParticipantInfo(
        userId: 'u1',
        displayName: 'Ada Lovelace',
        joinedAt: _startedAt,
        declinedAt: null,
        leftAt: _endedAt,
        state: ParticipantLiveState.left,
      ),
    ],
    bundles: [SessionBundleInfo(id: 'b1', name: 'Math')],
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

Future<void> _bootAuthenticated(WidgetTester tester) async {
  final tokens = AuthTokenStore()
    ..setSession(
      token: 'fake-token',
      account: const AccountInfo(
        homeAccountId: 'home-1',
        username: 'teacher@school.example',
        displayName: 'Teacher',
        department: null,
      ),
    );

  await tester.pumpWidget(
    AnchorDashboard(
      tokens: tokens,
      auth: _FakeAuth(),
      api: _dummyClient(),
      sessions: _FakeSessions(),
      bundles: _FakeBundles(),
      classes: ClassesApi(_dummyClient()),
      apiBaseUrl: Uri.parse('http://localhost'),
    ),
  );
  await tester.pumpAndSettle();
}

void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  testWidgets(
    'Home → History → open a past session renders the archived paper review '
    '(AD7, #172)',
    (tester) async {
      // A realistic window so the real fonts lay the composition out the way
      // they would in the product.
      tester.view.physicalSize = const Size(1400, 1000);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);

      await _bootAuthenticated(tester);

      // Real navigation: the teacher opens the History destination from the
      // shared app-bar (the flow a widget test of HistoryPage can't exercise).
      await tester.tap(find.byKey(const Key('nav-history')));
      await tester.pumpAndSettle();

      // The archive list renders the past session as a calm hairline row, marked
      // by the muted "Ended" outline badge — the archived counterpart of the
      // live page's magenta LIVE spark. No LIVE badge anywhere.
      expect(find.text('Math 101'), findsOneWidget);
      expect(find.widgetWithText(PlinkBadge, 'ENDED'), findsWidgets);
      expect(find.widgetWithText(PlinkBadge, 'LIVE'), findsNothing);

      // Open the read-only review for that session.
      await tester.tap(find.text('Math 101'));
      await tester.pumpAndSettle();

      // The review reads as the archived instrument panel: the audit-trail
      // sections render, and the pushed-through event lands in the log read-out.
      expect(find.text('Event log'), findsOneWidget);
      expect(find.text('Participants'), findsOneWidget);
      expect(find.text('BlockedUrl'), findsOneWidget);
      expect(find.text('Ada Lovelace'), findsWidgets);
      // Still muted: the review leads with "Ended", never the magenta spark.
      expect(find.widgetWithText(PlinkBadge, 'ENDED'), findsOneWidget);
      expect(find.widgetWithText(PlinkBadge, 'LIVE'), findsNothing);

      // The composition holds under the real shell + real fonts at this window
      // size — no RenderFlex overflow or other exception.
      expect(tester.takeException(), isNull);
    },
  );
}
