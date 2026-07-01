import 'dart:async';

import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/auth_token_store.dart';
import 'package:anchor_dashboard/api/bundles_api.dart';
import 'package:anchor_dashboard/api/classes_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/auth/msal_auth_service.dart';
import 'package:anchor_dashboard/main.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

// Boot-gate behaviour for session restore (#302). The router only knows you're
// signed in via the in-memory AuthTokenStore, which is empty on every page
// load — so on boot AnchorDashboard rehydrates it from MSAL behind a splash
// gate. These guard the gate's branches in isolation (no real fonts/window):
// the splash → shell transition when a cached session restores, and the fall-
// through to /login when MSAL reports no cached account.

ApiClient _dummyClient() => ApiClient(
  baseUrl: Uri.parse('http://localhost'),
  tokenProvider: () async => null,
);

const _cachedAccount = AccountInfo(
  homeAccountId: 'home-1',
  username: 'teacher@school.example',
  displayName: 'Ms Teacher',
  department: null,
);

// [account] is what MSAL reports as the cached account (null == signed out).
// [initGate], when supplied, holds MSAL init open so a test can observe the
// boot splash before rehydration resolves.
class _FakeAuth implements MsalAuthService {
  _FakeAuth({this.account, this.initGate});

  final AccountInfo? account;
  final Completer<void>? initGate;

  @override
  Future<void> initialize() => initGate?.future ?? Future<void>.value();
  @override
  Future<AccountInfo?> signIn() async => account;
  @override
  Future<void> signOut() async {}
  @override
  Future<String> acquireToken() async => 'fake-token';
  @override
  Future<String> acquireTokenSilent() async => 'fake-token';
  @override
  AccountInfo? currentAccount() => account;
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

Widget _app({required MsalAuthService auth, required AuthTokenStore tokens}) {
  return AnchorDashboard(
    tokens: tokens,
    auth: auth,
    api: _dummyClient(),
    sessions: _FakeSessions(),
    bundles: _FakeBundles(),
    classes: _FakeClasses(),
    apiBaseUrl: Uri.parse('http://localhost'),
  );
}

void main() {
  testWidgets(
    'shows the boot splash while restoring, then lands on the shell (#302)',
    (tester) async {
      final tokens = AuthTokenStore();
      final initGate = Completer<void>();

      await tester.pumpWidget(
        _app(
          auth: _FakeAuth(account: _cachedAccount, initGate: initGate),
          tokens: tokens,
        ),
      );

      // Rehydration is in flight (MSAL init held open): the quiet boot gate
      // shows a spinner and the login page has not been flashed.
      await tester.pump();
      expect(find.byType(CircularProgressIndicator), findsOneWidget);
      expect(find.byKey(const Key('login-headline')), findsNothing);
      expect(tokens.isAuthenticated, isFalse);

      // Let init finish: silent token acquisition repopulates the store and the
      // router moves us to the authenticated shell.
      initGate.complete();
      await tester.pumpAndSettle();

      expect(tokens.isAuthenticated, isTrue);
      expect(find.text('01 · HOME'), findsOneWidget);
      expect(find.byKey(const Key('login-headline')), findsNothing);
    },
  );

  testWidgets('no cached account → router redirects to /login (#302)', (
    tester,
  ) async {
    final tokens = AuthTokenStore();

    await tester.pumpWidget(_app(auth: _FakeAuth(), tokens: tokens));
    await tester.pumpAndSettle();

    // Nothing to restore: the gate resolves and we land on the login page.
    expect(tokens.isAuthenticated, isFalse);
    expect(find.byKey(const Key('login-headline')), findsOneWidget);
    expect(find.byKey(const Key('sign-in')), findsOneWidget);
    expect(find.text('01 · HOME'), findsNothing);
  });
}
