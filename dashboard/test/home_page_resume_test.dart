import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/auth_token_store.dart';
import 'package:anchor_dashboard/api/bundles_api.dart';
import 'package:anchor_dashboard/api/classes_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/auth/msal_auth_service.dart';
import 'package:anchor_dashboard/auth/msal_config.dart';
import 'package:anchor_dashboard/main.dart';
import 'package:anchor_dashboard/pages/home_page.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';

// Regression coverage for #126: a session left running when the teacher hits
// browser Back / refresh / relaunch must stay reachable from HomePage so it can
// be resumed and ended, instead of becoming an orphaned, uncontrollable session.

ApiClient _dummyClient() => ApiClient(
  baseUrl: Uri.parse('http://localhost'),
  tokenProvider: () async => null,
);

MsalAuthService _stubAuth() => MsalAuthService(
  const MsalConfig(
    tenantId: 'test-tenant',
    clientId: 'test-client',
    apiScope: 'api://test/.default',
  ),
);

class _FakeBundles extends BundlesApi {
  _FakeBundles() : super(_dummyClient());

  @override
  Future<List<BundleSummary>> list({bool includeArchived = false}) async =>
      const [];
}

class _FakeSessions extends SessionsApi {
  _FakeSessions({required this.active, this.classesList = const []})
      : super(_dummyClient());

  final List<ActiveSession> active;
  final List<ClassSummary> classesList;

  @override
  Future<MeResponse> me() async =>
      MeResponse(id: 'u1', displayName: 'Teacher', role: 'teacher');

  @override
  Future<List<ClassSummary>> classes() async => classesList;

  @override
  Future<List<ActiveSession>> activeSessions() async => active;
}

void main() {
  testWidgets(
    'HomePage surfaces a running session and Resume navigates back to it (#126)',
    (tester) async {
      final sessions = _FakeSessions(
        active: [
          ActiveSession(
            id: 'sess-1',
            classId: 'c1',
            startedAt: DateTime(2026, 6, 11, 9, 30),
          ),
        ],
        classesList: [
          ClassSummary(id: 'c1', name: 'Math 101', schoolYear: '2026'),
        ],
      );

      final home = HomePage(
        tokens: AuthTokenStore(),
        auth: _stubAuth(),
        sessions: sessions,
      );
      final router = GoRouter(
        initialLocation: '/',
        routes: [
          GoRoute(path: '/', builder: (_, _) => home),
          GoRoute(
            path: '/session/:id',
            builder: (_, state) => Scaffold(
              body: Text('SESSION ${state.pathParameters['id']}'),
            ),
          ),
        ],
      );

      await tester.pumpWidget(
        MaterialApp.router(
          // NoSplash keeps the tap below from loading Material's InkSparkle
          // fragment shader, which this Flutter SDK's test harness can't decode.
          theme: ThemeData(splashFactory: NoSplash.splashFactory),
          routerConfig: router,
        ),
      );
      await tester.pumpAndSettle();

      // The banner names the class (resolved from the loaded class list) and
      // offers a Resume affordance.
      expect(find.text('Active session'), findsOneWidget);
      expect(find.text('Math 101'), findsOneWidget);
      expect(find.text('Resume'), findsOneWidget);

      await tester.tap(find.text('Resume'));
      await tester.pumpAndSettle();

      // Resume carries the session id back into the URL/route.
      expect(find.text('SESSION sess-1'), findsOneWidget);
    },
  );

  testWidgets(
    'Relaunch with a session running: real app reaches it from HomePage (#126)',
    (tester) async {
      final tokens = AuthTokenStore()
        ..setSession(
          token: 't',
          account: const AccountInfo(
            homeAccountId: 'h',
            username: 'u',
            displayName: 'Teacher',
            department: null,
          ),
        );
      final api = ApiClient(
        baseUrl: Uri.parse('http://localhost'),
        tokenProvider: () async => tokens.token,
      );
      final sessions = _FakeSessions(
        active: [
          ActiveSession(
            id: 'sess-1',
            classId: 'c1',
            startedAt: DateTime(2026, 6, 11, 9, 30),
          ),
        ],
        classesList: [
          ClassSummary(id: 'c1', name: 'Math 101', schoolYear: '2026'),
        ],
      );

      await tester.pumpWidget(
        AnchorDashboard(
          tokens: tokens,
          auth: _stubAuth(),
          api: api,
          sessions: sessions,
          bundles: _FakeBundles(),
          classes: ClassesApi(api),
          apiBaseUrl: Uri.parse('http://localhost'),
        ),
      );
      await tester.pumpAndSettle();

      // A fresh app start (no session id in hand) still surfaces the running
      // session on Home — the refresh/relaunch half of the acceptance — through
      // the real router, redirect/auth gate, and HomePage composition. The
      // Resume→/session/:id navigation itself is covered by the focused test
      // above; we don't tap here because the real SessionPage's ink ripple would
      // hit the same undecodable shader (and its hub would never settle).
      expect(find.text('Active session'), findsOneWidget);
      expect(find.text('Math 101'), findsOneWidget);
      expect(find.text('Resume'), findsOneWidget);
    },
  );
}
