import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/auth_token_store.dart';
import 'package:anchor_dashboard/api/bundles_api.dart';
import 'package:anchor_dashboard/api/classes_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/auth/msal_auth_service.dart';
import 'package:anchor_dashboard/main.dart';
import 'package:anchor_dashboard/widgets/anchor_mark.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';

// Real-app e2e for the redesigned home page (AD3, #168): boots the actual
// AnchorDashboard authenticated, with a class to start and a still-running
// session, so the home composer and the instrument-panel resume card render
// inside the real shell with the real Fraunces / Space Mono fonts and the real
// window. A real-font overflow or a broken composition under the shell chrome
// can't hide behind the isolated widget tests. The Start→live-session and
// Resume taps aren't driven here — the real SessionPage's SignalR hub never
// settles in a test (see home_page_resume_test.dart) — those are covered by the
// widget tests against a fake route.

ApiClient _dummyClient() => ApiClient(
  baseUrl: Uri.parse('http://localhost'),
  tokenProvider: () async => null,
);

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
  AccountInfo? currentAccount() => null;
}

class _FakeSessions extends SessionsApi {
  _FakeSessions() : super(_dummyClient());

  @override
  Future<MeResponse> me() async =>
      MeResponse(id: 't1', displayName: 'Ms Teacher', role: 'Teacher');

  @override
  Future<List<ClassSummary>> classes() async => <ClassSummary>[
    ClassSummary(id: 'c1', name: 'Math 101', schoolYear: '2026'),
  ];

  @override
  Future<List<ActiveSession>> activeSessions() async => <ActiveSession>[
    ActiveSession(
      id: 'sess-1',
      classId: 'c1',
      startedAt: DateTime(2026, 6, 14, 9, 30),
    ),
  ];

  @override
  Future<List<SessionHistoryEntry>> history({
    int limit = 50,
    int offset = 0,
  }) async => const [];
}

class _FakeClasses extends ClassesApi {
  _FakeClasses() : super(_dummyClient());

  @override
  Future<List<String>> schools() async => const [];
}

class _FakeBundles extends BundlesApi {
  _FakeBundles() : super(_dummyClient());

  @override
  Future<List<BundleSummary>> list({bool includeArchived = false}) async =>
      const [];
}

void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  testWidgets(
    'home composer + resume card render under the real shell, no overflow (#168)',
    (tester) async {
      tester.view.physicalSize = const Size(1400, 1000);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);

      final tokens = AuthTokenStore()
        ..setSession(
          token: 'fake-token',
          account: const AccountInfo(
            homeAccountId: 'home-1',
            username: 'teacher@school.example',
            displayName: 'Ms Teacher',
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
          classes: _FakeClasses(),
          apiBaseUrl: Uri.parse('http://localhost'),
        ),
      );
      await tester.pumpAndSettle();

      // The shared chrome (AD1) frames the page: the lockup and the home
      // eyebrow render around it.
      expect(find.byType(AnchorLockup), findsOneWidget);
      expect(find.text('01 · HOME'), findsOneWidget);

      // The still-running session reads as the instrument-panel card: the mono
      // "Still running" eyebrow, the LIVE spark badge, the class name, and the
      // two ink actions (#126).
      expect(find.text('STILL RUNNING'), findsOneWidget);
      expect(find.text('LIVE'), findsOneWidget);
      expect(find.text('Math 101'), findsOneWidget);
      expect(find.text('Resume'), findsOneWidget);
      expect(find.text('End'), findsOneWidget);

      // The composer: the oversized Fraunces headline, the class picker, and the
      // single primary (magenta) Start action naming the class.
      expect(find.byKey(const Key('home-headline')), findsOneWidget);
      expect(find.byKey(const Key('class-picker')), findsOneWidget);
      expect(
        find.widgetWithText(ElevatedButton, 'Start session for Math 101'),
        findsOneWidget,
      );

      // No RenderFlex overflow or other exception with the real fonts at this
      // size — the bug an isolated widget test structurally misses.
      expect(tester.takeException(), isNull);
    },
  );
}
