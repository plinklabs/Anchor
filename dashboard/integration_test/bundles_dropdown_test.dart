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

// Real-app e2e for the bundle editor's match-type dropdown (#115).
//
// The bug is font-metric-sensitive: a 160px dropdown box was too narrow for the
// longest label "SignedPublisher", overflowing by ~25px with the real Roboto
// font. A widget test can't validate the *width* of the fix because the
// FlutterTest font distorts text metrics (it reports a far larger overflow), so
// the real check belongs here, under `flutter drive` with real fonts.
//
// Like live_session_test.dart, this boots the *real* AnchorDashboard (real
// router, real navigation, real layout, real fonts) wired to fake API
// subclasses, past the MSAL /login redirect via a seeded AuthTokenStore +
// no-op auth — the documented fallback since the dashboard can't be
// dev-impersonated.

ApiClient _dummyClient() => ApiClient(
  baseUrl: Uri.parse('http://localhost'),
  tokenProvider: () async => null,
);

/// Past the /login redirect without MSAL: the router only checks
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
  AccountInfo? currentAccount() => null;
}

/// Admin so the home screen exposes the "Bundles" nav action and the bundles
/// page renders the editor instead of the access-denied view.
class _FakeSessions extends SessionsApi {
  _FakeSessions() : super(_dummyClient());

  @override
  Future<MeResponse> me() async =>
      MeResponse(id: 'a1', displayName: 'Admin', role: 'Admin');

  @override
  Future<List<ClassSummary>> classes() async => [
    ClassSummary(id: 'c1', name: 'Math 101', schoolYear: '2025-2026'),
  ];

  @override
  Future<List<ActiveSession>> activeSessions() async => const [];
}

/// Serves one bundle whose only entry is an *app* matched by `SignedPublisher`
/// — the longest match-type label, and the one that overflowed (#115).
class _FakeBundles extends BundlesApi {
  _FakeBundles() : super(_dummyClient());

  static final _detail = BundleDetail(
    id: 'b1',
    name: 'Exam apps',
    version: 3,
    isArchived: false,
    hasBeenUsed: false,
    entries: [
      BundleEntry(
        kind: BundleEntryKind.app,
        value: 'Contoso.SignedApp',
        matchType: BundleEntryMatchType.signedPublisher,
      ),
    ],
  );

  @override
  Future<List<BundleSummary>> list({bool includeArchived = false}) async => [
    BundleSummary(
      id: _detail.id,
      name: _detail.name,
      version: _detail.version,
      isArchived: _detail.isArchived,
      hasBeenUsed: _detail.hasBeenUsed,
    ),
  ];

  @override
  Future<BundleDetail> get(String id) async => _detail;
}

void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  testWidgets(
    'admin opens a bundle with a SignedPublisher app entry; the match-type '
    'dropdown shows the full label with no overflow (#115)',
    (tester) async {
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
          classes: ClassesApi(_dummyClient()),
          apiBaseUrl: Uri.parse('http://localhost'),
        ),
      );
      await tester.pumpAndSettle();

      // Real navigation: the admin-only Bundles slot in the shared app-bar
      // (AD1, #166) routes to /bundles.
      final bundlesNav = find.byKey(const Key('nav-bundles'));
      expect(bundlesNav, findsOneWidget, reason: 'admin should see the Bundles nav');
      await tester.tap(bundlesNav);
      await tester.pumpAndSettle();

      // Open the seeded bundle, which renders the editor and the Apps entry
      // row's match-type dropdown.
      await tester.tap(find.text('Exam apps'));
      await tester.pumpAndSettle();

      // The selected label renders in full (real Roboto fits within the 200px
      // box) and no RenderFlex overflowed during layout.
      expect(find.text('SignedPublisher'), findsOneWidget);
      expect(tester.takeException(), isNull);
    },
  );
}
