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

// Real-app e2e for the paper redesign of the bundles editor (AD5, #170).
//
// A widget test renders the page under the FlutterTest font, which distorts
// metrics and composition; the brand reads correctly only with the real fonts
// and real navigation. So this boots the *real* AnchorDashboard (real router,
// real app-bar, real Fraunces/Hanken/Space Mono, real layout) wired to fake API
// subclasses, past the MSAL /login redirect via a seeded AuthTokenStore + no-op
// auth — the documented fallback since the dashboard can't be dev-impersonated.
//
// It asserts the redesign contracts: the catalogue renders bundles as hairline
// rows with the version as a mono spec chip, and opening a bundle yields exactly
// one magenta spark — the Save commit — with no layout overflow.

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
  Future<String> acquireTokenSilent() async => 'fake-token';
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
        kind: BundleEntryKind.domain,
        value: '*.geogebra.org',
        matchType: BundleEntryMatchType.wildcard,
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
    'admin opens the paper bundles editor: version reads as a mono spec chip '
    'and Save is the single magenta spark (#170)',
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

      // Real navigation: the admin-only Admin slot in the shared app-bar
      // (AD1, #166) opens the admin area, whose first sub-tab is Bundles (#299).
      final adminNav = find.byKey(const Key('nav-admin'));
      expect(
        adminNav,
        findsOneWidget,
        reason: 'admin should see the Admin nav',
      );
      await tester.tap(adminNav);
      await tester.pumpAndSettle();

      // The catalogue row carries the version as a mono badge (upper-cased).
      expect(find.widgetWithText(PlinkBadge, 'V3'), findsOneWidget);

      // Open the seeded bundle — renders the editor.
      await tester.tap(find.text('Exam apps'));
      await tester.pumpAndSettle();

      // Exactly one magenta spark on the page: the Save commit. Everything else
      // (New bundle, Add, Check, Delete/Archive) is calm ink.
      final save = find.byKey(const Key('bundles-save-button'));
      expect(save, findsOneWidget);
      expect(find.byType(ElevatedButton), findsOneWidget);
      expect(
        find.descendant(of: save, matching: find.text('Save')),
        findsOneWidget,
      );

      // Real fonts, real layout — no RenderFlex overflow anywhere on the page.
      expect(tester.takeException(), isNull);
    },
  );
}
