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

// Real-app e2e for creating and deleting a class from the Classes page (#152).
//
// The create/delete affordances live in the list header and roster header and
// drive dialogs over real navigation. A widget test covers the wiring in
// isolation; this boots the *real* AnchorDashboard (real fonts, real router,
// real window) wired to fake API subclasses so the dialog flow is exercised the
// way a teacher actually hits it — past the MSAL /login redirect via a seeded
// AuthTokenStore + no-op auth, the same pattern as classes_scope_row_test.dart.

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
  _FakeSessions(this._classes) : super(_dummyClient());

  final List<ClassSummary> _classes;

  @override
  Future<MeResponse> me() async =>
      MeResponse(id: 't1', displayName: 'Teacher', role: 'Teacher');

  @override
  Future<List<ClassSummary>> classes() async => List.of(_classes);

  @override
  Future<List<ActiveSession>> activeSessions() async => const [];
}

/// Server-side class store the fake API mutates so the page's reload-after-write
/// reflects creates and deletes, just like the real backend.
class _FakeClasses extends ClassesApi {
  _FakeClasses(this._store) : super(_dummyClient());

  final List<ClassSummary> _store;

  @override
  Future<ClassMembersResponse> members(String classId) async {
    final klass = _store.firstWhere((c) => c.id == classId);
    return ClassMembersResponse(
      id: klass.id,
      name: klass.name,
      schoolYear: klass.schoolYear,
      schoolTag: klass.schoolTag,
      classCode: klass.classCode,
      members: const [],
    );
  }

  @override
  Future<List<String>> schools() async => const ['SSM', 'SJI'];

  @override
  Future<ClassSummary> createClass({
    required String name,
    required String schoolYear,
    String? schoolTag,
    String? classCode,
  }) async {
    final created = ClassSummary(
      id: 'new-${_store.length}',
      name: name,
      schoolYear: schoolYear,
      schoolTag: schoolTag,
      classCode: classCode,
    );
    _store.add(created);
    return created;
  }

  @override
  Future<void> deleteClass(String classId) async {
    _store.removeWhere((c) => c.id == classId);
  }
}

void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  testWidgets(
    'teacher creates a class then deletes it from the Classes page (#152)',
    (tester) async {
      tester.view.physicalSize = const Size(1400, 1000);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);

      final store = <ClassSummary>[
        ClassSummary(id: 'c1', name: '3A', schoolYear: '2025-2026'),
      ];

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
          sessions: _FakeSessions(store),
          bundles: BundlesApi(_dummyClient()),
          classes: _FakeClasses(store),
          apiBaseUrl: Uri.parse('http://localhost'),
        ),
      );
      await tester.pumpAndSettle();

      // Real navigation from home into the Classes editor, via the shared
      // app-bar nav (AD1, #166).
      final classesNav = find.byKey(const Key('nav-classes'));
      expect(classesNav, findsOneWidget);
      await tester.tap(classesNav);
      await tester.pumpAndSettle();

      // Create a new class via the list-header "New" button + dialog.
      await tester.tap(find.widgetWithText(FilledButton, 'New'));
      await tester.pumpAndSettle();
      expect(find.widgetWithText(AlertDialog, 'New class'), findsOneWidget);
      await tester.enterText(find.widgetWithText(TextField, 'Name'), '4B');
      await tester.pump();
      await tester.tap(find.widgetWithText(FilledButton, 'Create'));
      await tester.pumpAndSettle();

      // The new class is selected and its roster header is showing.
      expect(find.text('4B (2025-2026)'), findsOneWidget);

      // Delete it again, through the confirm dialog.
      await tester.tap(find.widgetWithText(OutlinedButton, 'Delete class'));
      await tester.pumpAndSettle();
      expect(find.widgetWithText(AlertDialog, 'Delete class'), findsOneWidget);
      await tester.tap(find.widgetWithText(FilledButton, 'Delete'));
      await tester.pumpAndSettle();

      // Back to the original class auto-selected; the deleted one is gone, and
      // no overflow or other exception fired during the dialog flow.
      expect(find.text('4B (2025-2026)'), findsNothing);
      expect(find.text('3A (2025-2026)'), findsOneWidget);
      expect(tester.takeException(), isNull);
    },
  );
}
