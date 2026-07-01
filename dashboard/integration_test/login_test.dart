import 'dart:async';

import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/auth_token_store.dart';
import 'package:anchor_dashboard/api/bundles_api.dart';
import 'package:anchor_dashboard/api/classes_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/auth/msal_auth_service.dart';
import 'package:anchor_dashboard/main.dart';
import 'package:anchor_dashboard/widgets/anchor_mark.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';

// Real-app e2e for the redesigned sign-in page (AD2, #167): boots the actual
// AnchorDashboard with no session, so the router redirects to /login and the
// real login page renders with the real Fraunces / Space Mono fonts and the
// real window. Then it drives the single primary action — Microsoft sign-in —
// through a fake MSAL (which can't be dev-impersonated) and confirms the app
// lands on the authenticated shell. A real-font overflow or broken redirect
// wiring can't hide behind the isolated widget test.

ApiClient _dummyClient() => ApiClient(
  baseUrl: Uri.parse('http://localhost'),
  tokenProvider: () async => null,
);

// Returns a real account on sign-in so tapping the primary action drives the
// router redirect from /login into the shell, the way a real login does. When
// [hangAcquire] is set, the silent token step never completes — the day-old
// cached-session stall behind #303.
class _FakeAuth implements MsalAuthService {
  _FakeAuth({this.hangAcquire = false});

  final bool hangAcquire;

  @override
  Future<void> initialize() async {}
  @override
  Future<AccountInfo?> signIn() async => const AccountInfo(
    homeAccountId: 'home-1',
    username: 'teacher@school.example',
    displayName: 'Ms Teacher',
    department: null,
  );
  @override
  Future<void> signOut() async {}
  @override
  Future<String> acquireToken() {
    if (hangAcquire) return Completer<String>().future; // never completes
    return Future<String>.value('fake-token');
  }

  @override
  Future<String> acquireTokenSilent() {
    if (hangAcquire) return Completer<String>().future; // never completes
    return Future<String>.value('fake-token');
  }

  @override
  AccountInfo? currentAccount() => null;
}

class _FakeSessions extends SessionsApi {
  _FakeSessions() : super(_dummyClient());

  @override
  Future<MeResponse> me() async =>
      MeResponse(id: 't1', displayName: 'Ms Teacher', role: 'Teacher');

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
    'unauthenticated boot lands on the redesigned login, then signs in (#167)',
    (tester) async {
      tester.view.physicalSize = const Size(1400, 1000);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);

      await tester.pumpWidget(
        AnchorDashboard(
          tokens: AuthTokenStore(), // no session → redirect to /login
          auth: _FakeAuth(),
          api: _dummyClient(),
          sessions: _FakeSessions(),
          bundles: _FakeBundles(),
          classes: _FakeClasses(),
          apiBaseUrl: Uri.parse('http://localhost'),
        ),
      );
      await tester.pumpAndSettle();

      // The login chrome renders with the real fonts: lockup, the mono eyebrow,
      // the oversized Fraunces headline, and the single primary action.
      expect(find.byType(AnchorLockup), findsOneWidget);
      expect(find.text('ANCHOR FOR TEACHERS'), findsOneWidget);
      expect(find.byKey(const Key('login-headline')), findsOneWidget);
      expect(
        find.widgetWithText(ElevatedButton, 'Sign in with Microsoft'),
        findsOneWidget,
      );

      // Drive the single primary action → router redirects into the shell.
      await tester.tap(find.byKey(const Key('sign-in')));
      await tester.pumpAndSettle();

      // We're now on the authenticated shell (the home eyebrow + the shared
      // nav), and the login page is gone.
      expect(find.text('01 · HOME'), findsOneWidget);
      expect(find.byKey(const Key('nav-classes')), findsOneWidget);
      expect(find.byKey(const Key('sign-in')), findsNothing);

      // No RenderFlex overflow or other exception across the whole flow.
      expect(tester.takeException(), isNull);
    },
  );

  testWidgets(
    'a stalled silent token step settles into a retryable error, not a spinner '
    '(#303)',
    (tester) async {
      tester.view.physicalSize = const Size(1400, 1000);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);

      await tester.pumpWidget(
        AnchorDashboard(
          tokens: AuthTokenStore(), // no session → redirect to /login
          auth: _FakeAuth(hangAcquire: true),
          api: _dummyClient(),
          sessions: _FakeSessions(),
          bundles: _FakeBundles(),
          classes: _FakeClasses(),
          apiBaseUrl: Uri.parse('http://localhost'),
          // Short bound so the stalled silent step times out promptly here
          // instead of after the production 30s.
          loginSilentTimeout: const Duration(milliseconds: 200),
        ),
      );
      await tester.pumpAndSettle();

      // Drive the primary action. signIn resolves, but the silent token step
      // hangs — the real-world day-old-cache symptom.
      await tester.tap(find.byKey(const Key('sign-in')));
      await tester.pump();

      // Let the silent-timeout elapse and the error settle.
      await tester.pumpAndSettle(const Duration(milliseconds: 100));

      // We never reached the shell; we're still on login with a clear error and
      // no lingering spinner — sign-in is retryable, not stuck forever.
      expect(find.byKey(const Key('login-error')), findsOneWidget);
      expect(find.byKey(const Key('sign-in')), findsOneWidget);
      expect(find.text('01 · HOME'), findsNothing);
      expect(find.byType(CircularProgressIndicator), findsNothing);
      expect(tester.takeException(), isNull);
    },
  );
}
