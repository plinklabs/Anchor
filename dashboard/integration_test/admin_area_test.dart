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
import 'package:go_router/go_router.dart';
import 'package:integration_test/integration_test.dart';

// Real-app e2e for the admin area (#299): boots the actual AnchorDashboard
// (real router, real fonts, real window) and exercises the restructure end to
// end — the admin-gated top-level tab, the left vertical sub-nav, the legacy
// /bundles redirect, and the non-admin guard. Drives the chrome the way a
// teacher hits it so a broken nested-ShellRoute or a redirect regression can't
// hide behind isolated widget tests.

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
  _FakeSessions({required this.admin}) : super(_dummyClient());

  final bool admin;

  @override
  Future<MeResponse> me() async => MeResponse(
    id: 'u1',
    displayName: admin ? 'Admin' : 'Teacher',
    role: admin ? 'Admin' : 'Teacher',
  );

  @override
  Future<List<ClassSummary>> classes() async => const [];

  @override
  Future<List<ActiveSession>> activeSessions() async => const [];

  @override
  Future<List<SessionHistoryEntry>> history({
    int limit = 50,
    int offset = 0,
  }) async => const [];
}

class _FakeBundles extends BundlesApi {
  _FakeBundles() : super(_dummyClient());

  @override
  Future<List<BundleSummary>> list({bool includeArchived = false}) async =>
      const [];
}

AnchorDashboard _app({required bool admin}) {
  final tokens = AuthTokenStore()
    ..setSession(
      token: 'fake-token',
      account: const AccountInfo(
        homeAccountId: 'home-1',
        username: 'user@school.example',
        displayName: 'User',
        department: null,
      ),
    );
  return AnchorDashboard(
    tokens: tokens,
    auth: _FakeAuth(),
    api: _dummyClient(),
    sessions: _FakeSessions(admin: admin),
    bundles: _FakeBundles(),
    classes: ClassesApi(_dummyClient()),
    apiBaseUrl: Uri.parse('http://localhost'),
  );
}

void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  testWidgets(
    'admin: Admin tab opens the area, sub-nav shows Bundles, /bundles redirects',
    (tester) async {
      tester.view.physicalSize = const Size(1400, 1000);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);

      await tester.pumpWidget(_app(admin: true));
      await tester.pumpAndSettle();

      // The admin-only top-level tab is present; non-admin tabs aside.
      final adminNav = find.byKey(const Key('nav-admin'));
      expect(adminNav, findsOneWidget);

      // Opening it lands on the first sub-tab (Bundles) behind a left vertical
      // sub-nav, with the Bundles catalogue rendered in the content pane.
      await tester.tap(adminNav);
      await tester.pumpAndSettle();
      expect(find.text('04 · ADMIN'), findsOneWidget);
      expect(find.byKey(const Key('admin-nav-bundles')), findsOneWidget);
      expect(find.byKey(const Key('bundles-new-button')), findsOneWidget);

      // The legacy /bundles bookmark redirects to its new home under Admin.
      final ctx = tester.element(find.byType(AnchorLockup));
      GoRouter.of(ctx).go('/bundles');
      await tester.pumpAndSettle();
      expect(find.text('04 · ADMIN'), findsOneWidget);
      expect(find.byKey(const Key('admin-nav-bundles')), findsOneWidget);
      expect(find.byKey(const Key('bundles-new-button')), findsOneWidget);

      expect(tester.takeException(), isNull);
    },
  );

  testWidgets('non-admin: no Admin tab and /admin/* is redirected away', (
    tester,
  ) async {
    tester.view.physicalSize = const Size(1400, 1000);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);

    await tester.pumpWidget(_app(admin: false));
    await tester.pumpAndSettle();

    // No admin tab for a non-admin teacher.
    expect(find.byKey(const Key('nav-admin')), findsNothing);

    // Forcing the URL to an admin sub-page redirects back to Home — the
    // route-level half of the gating, so the area is never reachable.
    final ctx = tester.element(find.byType(AnchorLockup));
    GoRouter.of(ctx).go('/admin/bundles');
    await tester.pumpAndSettle();
    expect(find.text('01 · HOME'), findsOneWidget);
    expect(find.byKey(const Key('admin-nav-bundles')), findsNothing);
    expect(find.byKey(const Key('bundles-new-button')), findsNothing);

    expect(tester.takeException(), isNull);
  });
}
