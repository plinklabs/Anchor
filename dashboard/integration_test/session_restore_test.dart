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

// Real-app e2e for session restore on reload (#302): boots the actual
// AnchorDashboard with an *empty* AuthTokenStore — exactly the state a fresh
// page load starts in — but a fake MSAL that still reports a cached account and
// a silently-acquired token. The app must rehydrate the in-memory session and
// land straight on the authenticated shell with the real fonts / window, never
// flashing the login page. If silent acquisition fails instead, it must fall
// through to /login rather than hanging on the boot splash. A real-font
// overflow or broken boot-gate wiring can't hide behind the isolated widget
// tests.

ApiClient _dummyClient() => ApiClient(
  baseUrl: Uri.parse('http://localhost'),
  tokenProvider: () async => null,
);

const _cachedAccount = AccountInfo(
  homeAccountId: 'home-1',
  username: 'teacher@school.example',
  displayName: 'Ms Teacher',
  department: null,
);

// MSAL still holds the session (sessionStorage survives a reload): it reports a
// cached account, and silent acquisition succeeds — unless [silentFails], which
// models an expired session that would need interaction.
class _CachedSessionAuth implements MsalAuthService {
  _CachedSessionAuth({this.silentFails = false});

  final bool silentFails;

  @override
  Future<void> initialize() async {}
  @override
  Future<AccountInfo?> signIn() async => _cachedAccount;
  @override
  Future<void> signOut() async {}
  @override
  Future<String> acquireToken() async => 'fake-token';
  @override
  Future<String> acquireTokenSilent() async {
    if (silentFails) throw StateError('interaction required');
    return 'fake-token';
  }

  @override
  AccountInfo? currentAccount() => _cachedAccount;
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
  Future<List<ActiveSession>> activeSessions() async => const [];

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
    'a reload with a valid cached session lands on the shell, no re-login (#302)',
    (tester) async {
      tester.view.physicalSize = const Size(1400, 1000);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);

      // Empty store == fresh page load. Only MSAL's cached account + silent
      // token can get us past /login — proving the boot rehydration, not a
      // pre-seeded session.
      final tokens = AuthTokenStore();

      await tester.pumpWidget(
        AnchorDashboard(
          tokens: tokens,
          auth: _CachedSessionAuth(),
          api: _dummyClient(),
          sessions: _FakeSessions(),
          bundles: _FakeBundles(),
          classes: _FakeClasses(),
          apiBaseUrl: Uri.parse('http://localhost'),
        ),
      );
      await tester.pumpAndSettle();

      // We rehydrated and landed on the authenticated shell — the home eyebrow
      // and shared nav render, and the login page never showed.
      expect(tokens.isAuthenticated, isTrue);
      expect(find.text('01 · HOME'), findsOneWidget);
      expect(find.byType(AnchorLockup), findsOneWidget);
      expect(find.byKey(const Key('nav-classes')), findsOneWidget);
      expect(find.byKey(const Key('sign-in')), findsNothing);
      expect(find.byKey(const Key('login-headline')), findsNothing);

      // No RenderFlex overflow or other exception across the boot-gate → shell
      // transition with the real fonts.
      expect(tester.takeException(), isNull);
    },
  );

  testWidgets(
    'a cached account whose silent token has expired falls through to /login '
    '(#302)',
    (tester) async {
      tester.view.physicalSize = const Size(1400, 1000);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);

      final tokens = AuthTokenStore();

      await tester.pumpWidget(
        AnchorDashboard(
          tokens: tokens,
          auth: _CachedSessionAuth(silentFails: true),
          api: _dummyClient(),
          sessions: _FakeSessions(),
          bundles: _FakeBundles(),
          classes: _FakeClasses(),
          apiBaseUrl: Uri.parse('http://localhost'),
        ),
      );
      await tester.pumpAndSettle();

      // Silent acquisition failed → we never rehydrated, the boot gate resolved,
      // and the router sent us to the real login page (not a stuck splash).
      expect(tokens.isAuthenticated, isFalse);
      expect(find.byKey(const Key('login-headline')), findsOneWidget);
      expect(find.byKey(const Key('sign-in')), findsOneWidget);
      expect(find.text('01 · HOME'), findsNothing);
      expect(tester.takeException(), isNull);
    },
  );
}
