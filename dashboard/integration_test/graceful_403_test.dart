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

// Real-app e2e for the first-run / not-authorized state (#278): boots the
// actual AnchorDashboard as a teacher whose account isn't yet provisioned with
// the Teacher role. /me succeeds (any authenticated user) and provisions them,
// but the very next role-gated call — classes() on Home, history() on History —
// returns 403. This walks the real shell (real router, real Fraunces / Space
// Mono fonts, real window) across Home → Classes → History and asserts each
// surface shows the calm, human notice rather than a raw `ApiException(403):`.
// The composition + real-font rendering of that notice is exactly what the
// isolated widget tests can't see.

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

// A teacher whose Entra Teacher role isn't assigned yet: /me provisions them
// (returns a non-Teacher role), but every role-gated endpoint 403s.
class _UnprovisionedSessions extends SessionsApi {
  _UnprovisionedSessions() : super(_dummyClient());

  @override
  Future<MeResponse> me() async =>
      MeResponse(id: 't1', displayName: 'Ms Teacher', role: 'Member');

  @override
  Future<List<ClassSummary>> classes() async => throw ApiException(403, '');

  @override
  Future<List<ActiveSession>> activeSessions() async => throw ApiException(403, '');

  @override
  Future<List<SessionHistoryEntry>> history({
    int limit = 50,
    int offset = 0,
  }) async => throw ApiException(403, '');
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
    'first-run teacher (403) sees the calm not-authorized notice across '
    'Home / Classes / History, never a raw ApiException (#278)',
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
            displayName: 'Ms Teacher',
            department: null,
          ),
        );

      await tester.pumpWidget(
        AnchorDashboard(
          tokens: tokens,
          auth: _FakeAuth(),
          api: _dummyClient(),
          sessions: _UnprovisionedSessions(),
          bundles: _FakeBundles(),
          classes: _FakeClasses(),
          apiBaseUrl: Uri.parse('http://localhost'),
        ),
      );
      await tester.pumpAndSettle();

      const fragment = "isn't set up as a teacher yet";

      // Home: the composer's load 403'd — the notice replaces the raw exception
      // and the genuine "No classes assigned" empty state.
      expect(find.text('01 · HOME'), findsOneWidget);
      expect(find.textContaining(fragment), findsOneWidget);
      expect(find.textContaining('ApiException'), findsNothing);
      expect(find.text('No classes assigned to you yet.'), findsNothing);

      // Classes: the notice surfaces in place of "Pick a class on the left".
      await tester.tap(find.byKey(const Key('nav-classes')));
      await tester.pumpAndSettle();
      expect(find.text('02 · CLASSES'), findsOneWidget);
      expect(find.textContaining(fragment), findsOneWidget);
      expect(find.textContaining('ApiException'), findsNothing);
      expect(find.text('Pick a class on the left.'), findsNothing);

      // History: same calm notice.
      await tester.tap(find.byKey(const Key('nav-history')));
      await tester.pumpAndSettle();
      expect(find.text('03 · PAST SESSIONS'), findsOneWidget);
      expect(find.textContaining(fragment), findsOneWidget);
      expect(find.textContaining('ApiException'), findsNothing);

      // No RenderFlex overflow or other exception across the whole walk with the
      // real fonts at this size.
      expect(tester.takeException(), isNull);
    },
  );
}
