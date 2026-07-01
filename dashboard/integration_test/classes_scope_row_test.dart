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

/// Like [_FakeClasses] but its directory search fails until [failSchools] is
/// cleared — mirrors a missing Graph consent / 502 from `/directory/schools`
/// (#281). Counts calls so the test can prove Retry re-fetches.
class _FailingSchoolsClasses extends ClassesApi {
  _FailingSchoolsClasses() : super(_dummyClient());

  bool failSchools = true;
  int schoolsCalls = 0;

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
  Future<List<String>> schools() async {
    schoolsCalls++;
    if (failSchools) throw ApiException(502, 'directory unavailable');
    return const ['SSM', 'SJI'];
  }
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

      // Real navigation: the shared app-bar's Classes nav (AD1, #166) routes to
      // /classes, which auto-selects the first class and renders the editor.
      final classesNav = find.byKey(const Key('nav-classes'));
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

  testWidgets(
    'a failed school-tag load surfaces an inline error + Retry next to the '
    'School selector, and Retry recovers it (#281)',
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
            displayName: 'Teacher',
            department: null,
          ),
        );

      final classes = _FailingSchoolsClasses();

      await tester.pumpWidget(
        AnchorDashboard(
          tokens: tokens,
          auth: _FakeAuth(),
          api: _dummyClient(),
          sessions: _FakeSessions(),
          bundles: BundlesApi(_dummyClient()),
          classes: classes,
          apiBaseUrl: Uri.parse('http://localhost'),
        ),
      );
      await tester.pumpAndSettle();

      await tester.tap(find.byKey(const Key('nav-classes')));
      await tester.pumpAndSettle();

      // The selector renders, but the directory failure is surfaced inline with
      // a Retry — not swallowed into a silently empty dropdown (pre-#281).
      expect(find.text('School'), findsOneWidget);
      expect(find.textContaining("Couldn't load schools"), findsOneWidget);
      final retry = find.byKey(const Key('classes-schools-retry-button'));
      expect(retry, findsOneWidget);
      expect(find.textContaining('ApiException'), findsNothing);

      // Recover the backend, tap Retry — it re-fetches and clears the notice.
      final callsBefore = classes.schoolsCalls;
      classes.failSchools = false;
      await tester.tap(retry);
      await tester.pumpAndSettle();

      expect(classes.schoolsCalls, greaterThan(callsBefore));
      expect(find.textContaining("Couldn't load schools"), findsNothing);
      expect(
        find.byKey(const Key('classes-schools-retry-button')),
        findsNothing,
      );
      expect(tester.takeException(), isNull);
    },
  );
}
