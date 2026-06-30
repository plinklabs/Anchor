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

// Real-app e2e for the paper redesign of the classes / roster page (AD6, #171).
//
// A widget test renders the page under the FlutterTest font, which distorts
// metrics and composition; the brand reads correctly only with the real fonts
// and real navigation. So this boots the *real* AnchorDashboard (real router,
// real app-bar, real Fraunces/Hanken/Space Mono, real layout) wired to fake API
// subclasses, past the MSAL /login redirect via a seeded AuthTokenStore + no-op
// auth — the documented fallback since the dashboard can't be dev-impersonated.
//
// It asserts the redesign contracts: the roster renders as hairline rows (no
// Material DataTable) with the role as a mono spec chip, and the page yields
// exactly one magenta spark — the Save (scope) commit — with no layout overflow.

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
        members: [
          ClassMember(
            userId: 'u1',
            entraOid: '00000000-0000-0000-0000-000000000001',
            displayName: 'Alice Adams',
            userRole: 'Student',
            membershipRole: '0',
            joinedAt: DateTime.utc(2025, 9, 1),
          ),
          ClassMember(
            userId: 'u2',
            entraOid: '00000000-0000-0000-0000-000000000002',
            displayName: 'Bob Brown',
            userRole: 'Teacher',
            membershipRole: '1',
            joinedAt: DateTime.utc(2025, 9, 2),
          ),
        ],
      );

  @override
  Future<List<String>> schools() async => const ['SSM', 'SJI'];
}

void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  testWidgets(
    'teacher opens the paper classes page: roster reads as hairline rows with '
    'the role as a mono spec chip and Save is the single magenta spark (#171)',
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
      // /classes, which auto-selects the first class and renders the roster.
      final classesNav = find.byKey(const Key('nav-classes'));
      expect(classesNav, findsOneWidget);
      await tester.tap(classesNav);
      await tester.pumpAndSettle();

      // The roster renders as hairline rows — never a Material DataTable/Card.
      expect(find.byType(DataTable), findsNothing);
      expect(find.byType(Card), findsNothing);

      // Members read by name, with the role as a mono spec chip (upper-cased).
      expect(find.text('Alice Adams'), findsOneWidget);
      expect(find.text('Bob Brown'), findsOneWidget);
      expect(find.widgetWithText(PlinkBadge, 'STUDENT'), findsOneWidget);
      expect(find.widgetWithText(PlinkBadge, 'TEACHER'), findsOneWidget);

      // Exactly one magenta spark on the page: the Save (scope) commit.
      // New class, Import CSV, Populate from Graph, Delete class are calm ink.
      final save = find.byKey(const Key('classes-save-codes-button'));
      expect(save, findsOneWidget);
      expect(find.byType(ElevatedButton), findsOneWidget);

      // Real fonts, real layout — no RenderFlex overflow anywhere on the page.
      expect(tester.takeException(), isNull);
    },
  );
}
