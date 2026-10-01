import 'dart:async';

import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/auth_token_store.dart';
import 'package:anchor_dashboard/api/bundles_api.dart';
import 'package:anchor_dashboard/api/classes_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/auth/msal_auth_service.dart';
import 'package:anchor_dashboard/main.dart';
import 'package:anchor_dashboard/pages/classes_page.dart';
import 'package:anchor_dashboard/pages/history_page.dart';
import 'package:anchor_dashboard/pages/home_page.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';

// Deep links through the session-restore boot gate (#378). Since #302 the app
// boots behind a splash while it rehydrates the MSAL session, and that gate
// used to be a `MaterialApp(home:)`. Its Navigator reported `/` to the engine
// before the router existed, which on web overwrote the URL and made the
// engine forget the location the page was loaded at, so every deep link and
// every reload on a deep page opened Home. These boot the real AnchorDashboard
// at a deep location with a cached MSAL session and expect that page.

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

/// Starts the app "at" [location], as if the browser loaded that URL, and
/// returns the locations the app reports to the engine (the browser URL), in
/// order.
///
/// It also applies the web engine's rule for the launch location: Flutter can
/// read it as `defaultRouteName` only until the app first talks to the
/// `flutter/navigation` channel. The web engine then resets it to `/` (see
/// `EnginePlatformDispatcher` in the engine's web_ui platform_dispatcher.dart).
/// Without this the test binding would keep answering [location] forever and
/// the boot gate could never lose it. Messages still reach the real engine.
List<String> _loadAt(WidgetTester tester, String location) {
  final binding = tester.binding;
  final messenger = binding.defaultBinaryMessenger;
  final channel = SystemChannels.navigation;
  final reported = <String>[];

  binding.platformDispatcher.defaultRouteNameTestValue = location;
  addTearDown(binding.platformDispatcher.clearDefaultRouteNameTestValue);

  messenger.setMockMessageHandler(channel.name, (ByteData? message) {
    binding.platformDispatcher.clearDefaultRouteNameTestValue();
    final call = channel.codec.decodeMethodCall(message);
    if (call.method == 'routeInformationUpdated') {
      reported.add((call.arguments as Map<Object?, Object?>)['uri']! as String);
    }
    return messenger.delegate.send(channel.name, message);
  });
  addTearDown(() => messenger.setMockMessageHandler(channel.name, null));

  return reported;
}

void main() {
  testWidgets(
    'a reload on /classes with a cached session shows the splash, then Classes '
    '(#378)',
    (tester) async {
      final reported = _loadAt(tester, '/classes');
      final tokens = AuthTokenStore();
      final initGate = Completer<void>();

      await tester.pumpWidget(
        _app(
          auth: _FakeAuth(account: _cachedAccount, initGate: initGate),
          tokens: tokens,
        ),
      );
      await tester.pump();

      // The gate still does its #302 job: a quiet splash while MSAL init is in
      // flight, no login flash, and nothing written to the browser URL yet.
      expect(find.byType(CircularProgressIndicator), findsOneWidget);
      expect(find.byKey(const Key('login-headline')), findsNothing);
      expect(reported, isEmpty);

      initGate.complete();
      await tester.pumpAndSettle();

      // Once the session is restored the router opens the page the URL asked
      // for, and the URL keeps it.
      expect(tokens.isAuthenticated, isTrue);
      expect(find.byType(ClassesPage), findsOneWidget);
      expect(find.text('02 · CLASSES'), findsOneWidget);
      expect(find.byType(HomePage), findsNothing);
      expect(reported, isNot(contains('/')));
      expect(reported.last, '/classes');
    },
  );

  testWidgets('a deep link to /history opens History, not Home (#378)', (
    tester,
  ) async {
    final reported = _loadAt(tester, '/history');
    final tokens = AuthTokenStore();

    await tester.pumpWidget(
      _app(
        auth: _FakeAuth(account: _cachedAccount),
        tokens: tokens,
      ),
    );
    await tester.pumpAndSettle();

    expect(tokens.isAuthenticated, isTrue);
    expect(find.byType(HistoryPage), findsOneWidget);
    expect(find.byType(HomePage), findsNothing);
    expect(reported, isNot(contains('/')));
    expect(reported.last, '/history');
  });

  testWidgets(
    'a deep link with nothing to restore still goes to /login (#378)',
    (tester) async {
      final reported = _loadAt(tester, '/classes');
      final tokens = AuthTokenStore();

      await tester.pumpWidget(_app(auth: _FakeAuth(), tokens: tokens));
      await tester.pumpAndSettle();

      // Unchanged since #9: signed out means /login, whatever the URL asked.
      expect(tokens.isAuthenticated, isFalse);
      expect(find.byKey(const Key('login-headline')), findsOneWidget);
      expect(find.byType(ClassesPage), findsNothing);
      expect(reported.last, '/login');
    },
  );
}
