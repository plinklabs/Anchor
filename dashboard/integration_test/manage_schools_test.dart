import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/auth_token_store.dart';
import 'package:anchor_dashboard/api/bundles_api.dart';
import 'package:anchor_dashboard/api/classes_api.dart';
import 'package:anchor_dashboard/api/schools_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/auth/msal_auth_service.dart';
import 'package:anchor_dashboard/main.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';

// Real-app e2e for the "Schools" sub-tab (#301): boots the actual
// AnchorDashboard (real router, real fonts, real window) and drives the flow a
// real admin hits — open Admin, switch to the Schools sub-tab, then deactivate
// a school via its toggle. Exercises the route wiring, the sub-nav entry, and
// the page composition end to end so a broken nested route or a real-layout
// regression (e.g. the toggle overflowing the row) can't hide behind isolated
// widget tests.

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

class _FakeSchools extends SchoolsApi {
  _FakeSchools() : super(_dummyClient());

  List<School> schools = [
    School(name: 'Sint-Jan', isActive: true),
    School(name: 'Sint-Maria', isActive: true),
  ];

  final List<(String, bool)> calls = [];

  @override
  Future<List<School>> listSchools() async => schools;

  @override
  Future<School> setActive(String name, bool isActive) async {
    calls.add((name, isActive));
    final updated = School(name: name, isActive: isActive);
    schools = [
      for (final s in schools)
        if (s.name == name) updated else s,
    ];
    return updated;
  }
}

AnchorDashboard _app(_FakeSchools schools) {
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
    schools: schools,
    apiBaseUrl: Uri.parse('http://localhost'),
  );
}

void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  testWidgets('admin opens the Schools sub-tab and deactivates a school', (
    tester,
  ) async {
    tester.view.physicalSize = const Size(1400, 1000);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);

    final schools = _FakeSchools();
    await tester.pumpWidget(_app(schools));
    await tester.pumpAndSettle();

    // Into the admin area (lands on Bundles), then across to the Schools sub-tab.
    await tester.tap(find.byKey(const Key('nav-admin')));
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('admin-nav-schools')), findsOneWidget);

    await tester.tap(find.byKey(const Key('admin-nav-schools')));
    await tester.pumpAndSettle();

    // Schools render in the real composition with their toggles.
    expect(find.text('Sint-Jan'), findsOneWidget);
    expect(find.text('Sint-Maria'), findsOneWidget);

    // Deactivate one school.
    await tester.tap(find.byKey(const Key('school-toggle-Sint-Maria')));
    await tester.pumpAndSettle();
    expect(schools.calls, [('Sint-Maria', false)]);

    final toggle = tester.widget<Switch>(
      find.byKey(const Key('school-toggle-Sint-Maria')),
    );
    expect(toggle.value, isFalse);

    expect(tester.takeException(), isNull);
  });
}
