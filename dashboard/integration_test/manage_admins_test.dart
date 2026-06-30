import 'package:anchor_dashboard/api/admins_api.dart';
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

// Real-app e2e for the "Manage admins" sub-tab (#300): boots the actual
// AnchorDashboard (real router, real fonts, real window) and drives the flow a
// real admin hits — open Admin, switch to the Admins sub-tab, search a user,
// promote them, then remove an admin. Exercises the route wiring, the sub-nav
// entry, and the page composition end to end so a broken nested route or a
// real-layout regression can't hide behind isolated widget tests.

ApiClient _dummyClient() => ApiClient(
  baseUrl: Uri.parse('http://localhost'),
  tokenProvider: () async => null,
);

AdminUser _user(String id, String name, {String role = 'Admin'}) =>
    AdminUser(id: id, displayName: name, entraOid: '$id-oid', role: role);

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
      MeResponse(id: 'u1', displayName: 'Admin', role: 'Admin');

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

class _FakeAdmins extends AdminsApi {
  _FakeAdmins() : super(_dummyClient());

  List<AdminUser> admins = [
    _user('a1', 'Alice Admin'),
    _user('a2', 'Bob Admin'),
  ];

  final List<String> promoted = [];
  final List<String> demoted = [];

  @override
  Future<List<AdminUser>> listAdmins() async => admins;

  @override
  Future<List<AdminUser>> searchCandidates(String query) async {
    if (!'tina teacher'.contains(query.toLowerCase())) return const [];
    return [_user('t1', 'Tina Teacher', role: 'Teacher')];
  }

  @override
  Future<void> promote(String userId) async {
    promoted.add(userId);
    admins = [...admins, _user(userId, 'Tina Teacher')];
  }

  @override
  Future<void> demote(String userId) async {
    demoted.add(userId);
    admins = admins.where((a) => a.id != userId).toList();
  }
}

AnchorDashboard _app(_FakeAdmins admins) {
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
    sessions: _FakeSessions(),
    bundles: _FakeBundles(),
    classes: ClassesApi(_dummyClient()),
    admins: admins,
    apiBaseUrl: Uri.parse('http://localhost'),
  );
}

void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  testWidgets(
    'admin opens the Admins sub-tab, promotes a user, removes an admin',
    (tester) async {
      tester.view.physicalSize = const Size(1400, 1000);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);

      final admins = _FakeAdmins();
      await tester.pumpWidget(_app(admins));
      await tester.pumpAndSettle();

      // Into the admin area (lands on Bundles), then across to the Admins sub-tab.
      await tester.tap(find.byKey(const Key('nav-admin')));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('admin-nav-admins')), findsOneWidget);

      await tester.tap(find.byKey(const Key('admin-nav-admins')));
      await tester.pumpAndSettle();

      // Current admins render in the real composition.
      expect(find.text('Alice Admin'), findsOneWidget);
      expect(find.text('Bob Admin'), findsOneWidget);

      // Search + promote a signed-in user.
      await tester.enterText(
        find.byKey(const Key('manage-admins-search')),
        'tina',
      );
      await tester.pumpAndSettle();
      expect(find.text('Tina Teacher'), findsOneWidget);

      await tester.tap(find.byKey(const Key('admin-add-t1')));
      await tester.pumpAndSettle();
      expect(admins.promoted, ['t1']);
      expect(find.byKey(const Key('admin-remove-t1')), findsOneWidget);

      // Remove an admin (confirm dialog → demote).
      await tester.tap(find.byKey(const Key('admin-remove-a2')));
      await tester.pumpAndSettle();
      await tester.tap(find.widgetWithText(FilledButton, 'Remove'));
      await tester.pumpAndSettle();
      expect(admins.demoted, ['a2']);
      expect(find.text('Bob Admin'), findsNothing);

      expect(tester.takeException(), isNull);
    },
  );
}
