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

import 'support/e2e_binding.dart';

// Real-app e2e for the redesigned home page (AD3, #168): boots the actual
// AnchorDashboard authenticated, with a class to start and a still-running
// session, so the home composer and the instrument-panel resume card render
// inside the real shell with the real Fraunces / Space Mono fonts and the real
// window. A real-font overflow or a broken composition under the shell chrome
// can't hide behind the isolated widget tests. The Start→live-session and
// Resume taps aren't driven here — the real SessionPage's SignalR hub never
// settles in a test (see home_page_resume_test.dart) — those are covered by the
// widget tests against a fake route, and by live_session_test.dart with a
// stubbed hub. The composer's choice of when the session ends (#447) renders
// here in both its forms: the period ends, and a time to type.

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
  Future<String> acquireTokenSilent() async => 'fake-token';
  @override
  AccountInfo? currentAccount() => null;
}

class _FakeSessions extends SessionsApi {
  _FakeSessions({SessionEndOptions? endOptions})
    : endOptionsValue =
          endOptions ??
          SessionEndOptions(
            thisPeriodEndsAt: DateTime(2026, 6, 14, 10, 5),
            nextPeriodEndsAt: DateTime(2026, 6, 14, 10, 55),
          ),
      super(_dummyClient());

  final SessionEndOptions endOptionsValue;

  @override
  Future<SessionEndOptions> endOptions(String classId) async => endOptionsValue;

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

Widget _app(_FakeSessions sessions) {
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
  return AnchorDashboard(
    tokens: tokens,
    auth: _FakeAuth(),
    api: _dummyClient(),
    sessions: sessions,
    bundles: _FakeBundles(),
    classes: _FakeClasses(),
    apiBaseUrl: Uri.parse('http://localhost'),
  );
}

void main() {
  ensureE2eBinding();

  testWidgets(
    'home composer + resume card render under the real shell, no overflow (#168)',
    (tester) async {
      tester.view.physicalSize = const Size(1400, 1000);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);

      await tester.pumpWidget(_app(_FakeSessions()));
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
      // When the session ends (#447): the end of this period by default.
      expect(find.byKey(const Key('end-picker')), findsOneWidget);
      expect(find.text('At the end of this period (10:05)'), findsOneWidget);

      // No RenderFlex overflow or other exception with the real fonts at this
      // size — the bug an isolated widget test structurally misses.
      expect(tester.takeException(), isNull);
    },
  );

  testWidgets(
    'the choice of end time renders its period ends and its time field under '
    'the real shell, in a narrow window too (#447)',
    (tester) async {
      tester.view.physicalSize = const Size(1400, 1000);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);

      await tester.pumpWidget(_app(_FakeSessions()));
      await tester.pumpAndSettle();

      // The menu: both period ends and a time of the teacher's choosing.
      await tester.tap(find.byKey(const Key('end-picker')));
      await tester.pumpAndSettle();
      expect(
        find.text('At the end of the next period (10:55)'),
        findsOneWidget,
      );
      await tester.tap(find.text('At a time I pick').last);
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('end-time')), findsOneWidget);
      expect(tester.takeException(), isNull);

      // A school without lesson periods, on a narrow window: only the time.
      tester.view.physicalSize = const Size(750, 900);
      await tester.pumpWidget(
        _app(_FakeSessions(endOptions: SessionEndOptions())),
      );
      await tester.pumpAndSettle();
      expect(find.text('At a time I pick'), findsOneWidget);
      expect(find.byKey(const Key('end-time')), findsOneWidget);
      expect(find.textContaining('period'), findsNothing);
      expect(tester.takeException(), isNull);
    },
  );
}
