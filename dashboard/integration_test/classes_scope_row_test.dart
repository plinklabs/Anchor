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

// Real-app e2e for the class editor's scope + action rows (#151).
//
// The bug is layout-/font-sensitive: a fixed-width `Row` of School (220) +
// Class code (160) + Save (~492px total) overflowed the right pane by ~103px on
// a narrow window. The widget test reproduces the *structural* overflow, but
// the FlutterTest font distorts text metrics, so the real-width check — does the
// reflowed `Wrap` actually fit the real Roboto controls in a narrow pane —
// belongs here, under `flutter drive` with real fonts and real navigation.
//
// Like bundles_dropdown_test.dart, this boots the *real* AnchorDashboard wired
// to fake API subclasses, past the MSAL /login redirect via a seeded
// AuthTokenStore + no-op auth.

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

class _FakeSessions extends SessionsApi {
  _FakeSessions() : super(_dummyClient());

  @override
  Future<MeResponse> me() async =>
      MeResponse(id: 't1', displayName: 'Teacher', role: 'Teacher');

  @override
  Future<List<ClassSummary>> classes() async => [
    ClassSummary(
      id: 'c1',
      name: '3A',
      schoolYear: '2025-2026',
      schoolTag: 'SSM',
      classCode: '3A',
    ),
  ];

  @override
  Future<List<ActiveSession>> activeSessions() async => const [];
}

class _FakeClasses extends ClassesApi {
  _FakeClasses() : super(_dummyClient());

  @override
  Future<ClassMembersResponse> members(String classId) async =>
      ClassMembersResponse(
        id: 'c1',
        name: '3A',
        schoolYear: '2025-2026',
        schoolTag: 'SSM',
        classCode: '3A',
        members: const [],
      );

  @override
  Future<List<String>> schools() async => const ['SSM', 'SJI'];
}

void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  testWidgets(
    'teacher opens a class on a narrow window; the scope + action rows reflow '
    'with no overflow (#151)',
    (tester) async {
      // Narrow the window so the editor pane is constrained: list (260) +
      // divider (1) + 16px padding each side leave ~457px at 750px — wide enough
      // for the 420px search field but narrower than the ~492px scope row, the
      // condition that produced the reported ~103px overflow.
      tester.view.physicalSize = const Size(750, 900);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);

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
          bundles: BundlesApi(_dummyClient()),
          classes: _FakeClasses(),
          apiBaseUrl: Uri.parse('http://localhost'),
        ),
      );
      await tester.pumpAndSettle();

      // Real navigation: the home AppBar's "Manage classes" action routes to
      // /classes, which auto-selects the first class and renders the editor.
      final classesNav = find.widgetWithText(TextButton, 'Manage classes');
      expect(classesNav, findsOneWidget);
      await tester.tap(classesNav);
      await tester.pumpAndSettle();

      // The scope + action controls all rendered with the real font...
      expect(find.text('School'), findsOneWidget);
      expect(find.text('Class code'), findsOneWidget);
      expect(find.text('Save'), findsOneWidget);
      expect(find.text('Import CSV'), findsOneWidget);
      expect(find.text('Populate from Graph'), findsOneWidget);

      // ...and no RenderFlex overflowed during layout.
      expect(tester.takeException(), isNull);
    },
  );
}
