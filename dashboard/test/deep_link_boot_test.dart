import 'dart:async';

import 'package:anchor_dashboard/api/admins_api.dart';
import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/auth_token_store.dart';
import 'package:anchor_dashboard/api/bundles_api.dart';
import 'package:anchor_dashboard/api/classes_api.dart';
import 'package:anchor_dashboard/api/schools_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/auth/msal_auth_service.dart';
import 'package:anchor_dashboard/main.dart';
import 'package:anchor_dashboard/pages/bundles_page.dart';
import 'package:anchor_dashboard/pages/classes_page.dart';
import 'package:anchor_dashboard/pages/history_page.dart';
import 'package:anchor_dashboard/pages/home_page.dart';
import 'package:anchor_dashboard/pages/past_session_page.dart';
import 'package:anchor_dashboard/router.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';

// Deep links through the session-restore boot gate (#378). Since #302 the app
// boots behind a splash while it rehydrates the MSAL session, and that gate
// used to be a `MaterialApp(home:)`. Its Navigator reported `/` to the engine
// before the router existed, which on web overwrote the URL and made the
// engine forget the location the page was loaded at, so every deep link and
// every reload on a deep page opened Home. These boot the real AnchorDashboard
// at a deep location with a cached MSAL session and expect that page.
//
// A signed-out deep link (#379) goes to /login with the page it asked for in
// `?from=`, and signing in returns there instead of Home. Only a known in-app
// location is honored, so the login page can't be used as an open redirect.

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
// boot splash before rehydration resolves. [signInAccount] is what the sign-in
// popup returns, for a teacher who signs in on /login. [silentExpired] makes
// the boot-time silent token step fail, as for a cached session that has
// expired or needs interaction.
class _FakeAuth implements MsalAuthService {
  _FakeAuth({
    this.account,
    this.initGate,
    this.signInAccount,
    this.silentExpired = false,
  });

  final AccountInfo? account;
  final Completer<void>? initGate;
  final AccountInfo? signInAccount;
  final bool silentExpired;

  @override
  Future<void> initialize() => initGate?.future ?? Future<void>.value();
  @override
  Future<AccountInfo?> signIn() async => signInAccount ?? account;
  @override
  Future<void> signOut() async {}
  @override
  Future<String> acquireToken() async => 'fake-token';
  @override
  Future<String> acquireTokenSilent() async {
    if (silentExpired) throw StateError('interaction_required');
    return 'fake-token';
  }

  @override
  AccountInfo? currentAccount() => account;
}

class _FakeSessions extends SessionsApi {
  _FakeSessions({this.role = 'Teacher'}) : super(_dummyClient());

  final String role;

  @override
  Future<MeResponse> me() async =>
      MeResponse(id: 't1', displayName: 'Ms Teacher', role: role);

  @override
  Future<List<ClassSummary>> classes() async => const [];

  @override
  Future<List<ActiveSession>> activeSessions() async => const [];

  @override
  Future<List<SessionHistoryEntry>> history({
    int limit = 50,
    int offset = 0,
  }) async => const [];

  // Every session belongs to another teacher: since #369 its detail and its
  // unblock requests answer 403.
  @override
  Future<SessionDetail> getSession(String sessionId) async =>
      throw ApiException(403, '');

  @override
  Future<List<UnblockRequestSummary>> unblockRequests(String sessionId) async =>
      throw ApiException(403, '');
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

Widget _app({
  required MsalAuthService auth,
  required AuthTokenStore tokens,
  String role = 'Teacher',
}) {
  return AnchorDashboard(
    tokens: tokens,
    auth: auth,
    api: _dummyClient(),
    sessions: _FakeSessions(role: role),
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
    'a signed-out deep link goes to /login, then back to the page after '
    'sign-in (#379)',
    (tester) async {
      final reported = _loadAt(tester, '/classes');
      final tokens = AuthTokenStore();

      await tester.pumpWidget(
        _app(
          auth: _FakeAuth(signInAccount: _cachedAccount),
          tokens: tokens,
        ),
      );
      await tester.pumpAndSettle();

      // Signed out still means /login (#9), but the URL keeps the page that
      // was asked for.
      expect(tokens.isAuthenticated, isFalse);
      expect(find.byKey(const Key('login-headline')), findsOneWidget);
      expect(find.byType(ClassesPage), findsNothing);
      expect(reported.last, '/login?from=%2Fclasses');

      await _signIn(tester);

      expect(tokens.isAuthenticated, isTrue);
      expect(find.byType(ClassesPage), findsOneWidget);
      expect(find.text('02 · CLASSES'), findsOneWidget);
      expect(find.byType(HomePage), findsNothing);
      expect(reported.last, '/classes');
      expect(reported, isNot(contains('/')));
    },
  );

  testWidgets(
    'a reload whose session needs interaction returns to that page after '
    'sign-in (#379)',
    (tester) async {
      final reported = _loadAt(tester, '/history');
      final tokens = AuthTokenStore();

      // MSAL still has the account, but the silent token step fails, so the
      // #302 restore gives up and the router sends the teacher to /login.
      await tester.pumpWidget(
        _app(
          auth: _FakeAuth(account: _cachedAccount, silentExpired: true),
          tokens: tokens,
        ),
      );
      await tester.pumpAndSettle();

      expect(tokens.isAuthenticated, isFalse);
      expect(find.byKey(const Key('login-headline')), findsOneWidget);
      expect(reported.last, '/login?from=%2Fhistory');

      await _signIn(tester);

      expect(find.byType(HistoryPage), findsOneWidget);
      expect(find.byType(HomePage), findsNothing);
      expect(reported.last, '/history');
    },
  );

  testWidgets(
    'a reload of /login?from= with a cached session opens that page (#379)',
    (tester) async {
      final reported = _loadAt(tester, '/login?from=%2Fclasses');
      final tokens = AuthTokenStore();

      // The #302 restore signs the teacher in behind the splash, and the
      // router takes them from /login to the page in `from`.
      await tester.pumpWidget(
        _app(
          auth: _FakeAuth(account: _cachedAccount),
          tokens: tokens,
        ),
      );
      await tester.pumpAndSettle();

      expect(tokens.isAuthenticated, isTrue);
      expect(find.byType(ClassesPage), findsOneWidget);
      expect(find.byKey(const Key('login-headline')), findsNothing);
      expect(reported.last, '/classes');
    },
  );

  testWidgets(
    'a signed-out link to Home goes to plain /login and back to Home (#379)',
    (tester) async {
      final reported = _loadAt(tester, '/');
      final tokens = AuthTokenStore();

      await tester.pumpWidget(
        _app(
          auth: _FakeAuth(signInAccount: _cachedAccount),
          tokens: tokens,
        ),
      );
      await tester.pumpAndSettle();
      expect(reported.last, '/login');

      await _signIn(tester);
      expect(find.byType(HomePage), findsOneWidget);
      expect(reported.last, '/');
    },
  );

  testWidgets(
    'a signed-out link to an admin page brings a teacher to Home, not the '
    'admin area (#379)',
    (tester) async {
      final reported = _loadAt(tester, '/admin/bundles');
      final tokens = AuthTokenStore();

      await tester.pumpWidget(
        _app(
          auth: _FakeAuth(signInAccount: _cachedAccount),
          tokens: tokens,
        ),
      );
      await tester.pumpAndSettle();
      expect(reported.last, '/login?from=%2Fadmin%2Fbundles');

      await _signIn(tester);

      // The admin gate (#299) sends a non-admin Home, as it does for a
      // signed-in teacher who follows the same link.
      expect(find.byType(BundlesPage), findsNothing);
      expect(find.byType(HomePage), findsOneWidget);
      expect(reported.last, '/');
      expect(tester.takeException(), isNull);
    },
  );

  testWidgets(
    'a signed-out link to an admin page opens it for an admin after sign-in '
    '(#379)',
    (tester) async {
      final reported = _loadAt(tester, '/admin/bundles');
      final tokens = AuthTokenStore();

      await tester.pumpWidget(
        _app(
          auth: _FakeAuth(signInAccount: _cachedAccount),
          tokens: tokens,
          role: 'Admin',
        ),
      );
      await tester.pumpAndSettle();
      await _signIn(tester);

      expect(find.byType(BundlesPage), findsOneWidget);
      expect(reported.last, '/admin/bundles');
    },
  );

  testWidgets(
    "a signed-out link to another teacher's session opens its page, which "
    'says calmly that it is not available (#379, #382)',
    (tester) async {
      final reported = _loadAt(tester, '/history/s-other');
      final tokens = AuthTokenStore();

      await tester.pumpWidget(
        _app(
          auth: _FakeAuth(signInAccount: _cachedAccount),
          tokens: tokens,
        ),
      );
      await tester.pumpAndSettle();
      expect(reported.last, '/login?from=%2Fhistory%2Fs-other');

      await _signIn(tester);

      // The session is 403 for a teacher who doesn't own it (#369). The page
      // says so calmly inside the shell (#382), with nothing of the raw
      // exception, and the nav still works: no crash, no endless spinner.
      expect(find.byType(PastSessionPage), findsOneWidget);
      expect(find.text('PAST SESSION'), findsOneWidget);
      expect(
        find.text(
          "This session isn't available to you. Only the teacher who started "
          'it can open it.',
        ),
        findsOneWidget,
      );
      expect(find.textContaining('ApiException'), findsNothing);
      expect(find.byType(CircularProgressIndicator), findsNothing);
      expect(reported.last, '/history/s-other');
      expect(tester.takeException(), isNull);

      await tester.tap(find.byKey(const Key('nav-home')));
      await tester.pumpAndSettle();
      expect(find.byType(HomePage), findsOneWidget);
    },
  );

  testWidgets('signing out goes to plain /login, without the page (#379)', (
    tester,
  ) async {
    final reported = _loadAt(tester, '/classes');
    final tokens = AuthTokenStore();

    await tester.pumpWidget(
      _app(
        auth: _FakeAuth(account: _cachedAccount),
        tokens: tokens,
      ),
    );
    await tester.pumpAndSettle();
    expect(find.byType(ClassesPage), findsOneWidget);

    await tester.tap(find.byKey(const Key('sign-out')));
    await tester.pumpAndSettle();

    // An explicit sign-out hands the machine to whoever signs in next, so it
    // doesn't keep this teacher's page for them.
    expect(find.byKey(const Key('login-headline')), findsOneWidget);
    expect(reported.last, '/login');
  });

  // Each of these is written into `/login?from=` by hand, as a phishing link
  // would be. Signing in must stay in the app and land on Home.
  for (final from in _hostileFroms) {
    testWidgets(
      'sign-in ignores from=${Uri.encodeComponent(from)} and lands on Home '
      '(#379)',
      (tester) async {
        final location = Uri(
          path: '/login',
          queryParameters: <String, String>{loginFromParameter: from},
        ).toString();
        final reported = _loadAt(tester, location);
        final tokens = AuthTokenStore();

        await tester.pumpWidget(
          _app(
            auth: _FakeAuth(signInAccount: _cachedAccount),
            tokens: tokens,
          ),
        );
        await tester.pumpAndSettle();
        expect(find.byKey(const Key('login-headline')), findsOneWidget);

        await _signIn(tester);

        expect(tokens.isAuthenticated, isTrue);
        expect(find.byType(HomePage), findsOneWidget);
        expect(reported.last, '/');
      },
    );
  }

  group('safeReturnLocation (#379)', () {
    late RouteConfiguration routes;

    setUp(() {
      routes = buildRouter(
        tokens: AuthTokenStore(),
        auth: _FakeAuth(),
        sessions: _FakeSessions(),
        bundles: _FakeBundles(),
        classes: _FakeClasses(),
        admins: AdminsApi(_dummyClient()),
        schools: SchoolsApi(_dummyClient()),
        apiBaseUrl: Uri.parse('http://localhost'),
      ).configuration;
    });

    test('accepts every page of the app', () {
      for (final location in <String>[
        '/',
        '/classes',
        '/history',
        '/history/s-1',
        '/session/s-1',
        '/admin',
        '/admin/bundles',
        '/admin/admins',
        '/admin/schools',
        '/bundles',
      ]) {
        expect(
          safeReturnLocation(location, routes: routes),
          location,
          reason: location,
        );
      }
    });

    test('keeps the query, drops a fragment, and resolves dot segments', () {
      expect(
        safeReturnLocation('/classes?tab=roster#x', routes: routes),
        '/classes?tab=roster',
      );
      expect(
        safeReturnLocation('/history/../classes', routes: routes),
        '/classes',
      );
    });

    test('rejects anything that could leave the app', () {
      for (final from in <String?>[
        null,
        ..._hostileFroms,
        'HTTPS://evil.example',
        'https:/evil.example',
        '///evil.example',
        '/%5Cevil.example',
        '/\nevil.example',
        '/history/s-1\u007f',
        'mailto:x@evil.example',
        ' /classes',
        '/Classes',
      ]) {
        expect(
          safeReturnLocation(from, routes: routes),
          isNull,
          reason: '$from',
        );
      }
    });

    test('loginLocationFor carries a known page and nothing else', () {
      expect(
        loginLocationFor(Uri.parse('/session/s-1'), routes: routes),
        '/login?from=%2Fsession%2Fs-1',
      );
      expect(loginLocationFor(Uri.parse('/'), routes: routes), '/login');
      expect(loginLocationFor(Uri.parse('/nope'), routes: routes), '/login');
    });
  });
}

/// `from` values that must never take a teacher anywhere after sign-in: other
/// sites (absolute, scheme-relative, backslash and tab tricks a browser reads
/// as `//host`, dot segments that climb to one), other schemes, a relative or
/// unknown path, and the login page itself.
const _hostileFroms = <String>[
  'https://evil.example/classes',
  'http://evil.example',
  '//evil.example/classes',
  r'/\evil.example',
  '/\t/evil.example',
  '/../evil.example',
  '/..//evil.example',
  'javascript:alert(1)',
  'data:text/html,hi',
  'classes',
  '/nope',
  '/login',
  '/login?from=%2Fclasses',
  '',
];

/// Taps the sign-in button on /login, as the teacher does, and lets the fake
/// MSAL popup finish.
Future<void> _signIn(WidgetTester tester) async {
  await tester.tap(find.byKey(const Key('sign-in')));
  await tester.pumpAndSettle();
}
