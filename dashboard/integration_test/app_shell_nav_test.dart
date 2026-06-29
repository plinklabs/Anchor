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

// Real-app e2e for the shared dashboard chrome (AD1, #166): boots the actual
// AnchorDashboard (real router, real Fraunces/Space Mono fonts, real window)
// past the /login redirect and walks the app-bar nav across pages. Drives the
// chrome the way a teacher hits it so a real-font overflow or a broken
// ShellRoute wiring can't hide behind isolated widget tests.

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

// Admin so the chrome shows every standing nav slot, including Admin.
class _FakeSessions extends SessionsApi {
  _FakeSessions() : super(_dummyClient());

  @override
  Future<MeResponse> me() async =>
      MeResponse(id: 'a1', displayName: 'Admin', role: 'Admin');

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
    'shared app-bar nav carries the chrome across pages, no overflow (#166)',
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
            username: 'admin@school.example',
            displayName: 'Admin',
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

      // Home: the lockup + the standing nav (Admin included, admin) all
      // render, and the page eyebrow shows the section.
      expect(find.byType(AnchorLockup), findsOneWidget);
      expect(find.byKey(const Key('nav-home')), findsOneWidget);
      expect(find.byKey(const Key('nav-classes')), findsOneWidget);
      expect(find.byKey(const Key('nav-history')), findsOneWidget);
      expect(find.byKey(const Key('nav-admin')), findsOneWidget);
      expect(find.text('01 · HOME'), findsOneWidget);
      expect(find.text('Admin'), findsOneWidget);

      // Navigate to Classes via the shared nav; the chrome persists and the
      // eyebrow follows.
      await tester.tap(find.byKey(const Key('nav-classes')));
      await tester.pumpAndSettle();
      expect(find.text('02 · CLASSES'), findsOneWidget);
      expect(find.byType(AnchorLockup), findsOneWidget);

      // And to History.
      await tester.tap(find.byKey(const Key('nav-history')));
      await tester.pumpAndSettle();
      expect(find.text('03 · PAST SESSIONS'), findsOneWidget);

      // The lockup returns to Home.
      await tester.tap(find.byKey(const Key('lockup-home')));
      await tester.pumpAndSettle();
      expect(find.text('01 · HOME'), findsOneWidget);

      // No RenderFlex overflow or other exception fired with the real fonts
      // across the whole walk.
      expect(tester.takeException(), isNull);
    },
  );
}
