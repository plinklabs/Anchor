import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/auth_token_store.dart';
import 'package:anchor_dashboard/api/bundles_api.dart';
import 'package:anchor_dashboard/api/classes_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/auth/msal_auth_service.dart';
import 'package:anchor_dashboard/main.dart';
import 'package:anchor_dashboard/pages/classes_page.dart';
import 'package:anchor_dashboard/pages/home_page.dart';
import 'package:anchor_dashboard/pages/past_session_page.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';

import 'support/e2e_binding.dart';

// Real-app e2e for deep links and reloads (#378). A teacher who reloads a page,
// opens a bookmark, or follows a link loads the dashboard at a hash route with
// an empty AuthTokenStore and a cached MSAL session. Since #302 the app first
// restores that session behind a boot gate, and the gate used to send `/` to
// the engine before the router started, so every such load opened Home. This
// boots the actual AnchorDashboard "at" a deep location with the real fonts and
// window, and expects that page once the session is restored. In a real
// browser it also checks that the URL still holds the route.

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

final _startedAt = DateTime.utc(2026, 6, 12, 9, 15);
final _endedAt = DateTime.utc(2026, 6, 12, 10, 5);

// MSAL still holds the session (it survives a reload): a cached account and a
// silently acquired token.
class _CachedSessionAuth implements MsalAuthService {
  @override
  Future<void> initialize() async {}
  @override
  Future<AccountInfo?> signIn() async => _cachedAccount;
  @override
  Future<void> signOut() async {}
  @override
  Future<String> acquireToken() async => 'fake-token';
  @override
  Future<String> acquireTokenSilent() async => 'fake-token';
  @override
  AccountInfo? currentAccount() => _cachedAccount;
}

class _FakeSessions extends SessionsApi {
  _FakeSessions() : super(_dummyClient());

  @override
  Future<MeResponse> me() async =>
      MeResponse(id: 't1', displayName: 'Ms Teacher', role: 'Teacher');

  @override
  Future<List<ClassSummary>> classes() async => <ClassSummary>[
    ClassSummary(id: 'c1', name: 'Math 101', schoolYear: '2026'),
  ];

  @override
  Future<List<ActiveSession>> activeSessions() async => const [];

  @override
  Future<List<SessionHistoryEntry>> history({
    int limit = 50,
    int offset = 0,
  }) async => const [];

  @override
  Future<SessionDetail> getSession(String sessionId) async => SessionDetail(
    id: sessionId,
    classId: 'c1',
    className: 'Math 101',
    joinCode: 'ABC123',
    startedAt: _startedAt,
    endedAt: _endedAt,
    summaries: const [],
    recentEvents: const [],
    participants: const [],
    bundles: const [],
    grants: const [],
  );

  @override
  Future<List<UnblockRequestSummary>> unblockRequests(String sessionId) async =>
      const [];
}

class _FakeClasses extends ClassesApi {
  _FakeClasses() : super(_dummyClient());

  @override
  Future<ClassMembersResponse> members(String classId) async =>
      ClassMembersResponse(
        id: classId,
        name: 'Math 101',
        schoolYear: '2026',
        members: const [],
      );

  @override
  Future<List<String>> schools() async => const [];
}

class _FakeBundles extends BundlesApi {
  _FakeBundles() : super(_dummyClient());

  @override
  Future<List<BundleSummary>> list({bool includeArchived = false}) async =>
      const [];
}

/// Starts the app "at" [location], as if the browser loaded that URL.
///
/// It also applies the web engine's rule for the launch location: Flutter can
/// read it as `defaultRouteName` only until the app first talks to the
/// `flutter/navigation` channel. The web engine then resets it to `/` (see
/// `EnginePlatformDispatcher` in the engine's web_ui platform_dispatcher.dart).
/// Without this the test binding would keep answering [location] forever and
/// the boot gate could never lose it. Messages still reach the real engine, so
/// in a browser the URL bar updates for real.
void _loadAt(WidgetTester tester, String location) {
  final binding = tester.binding;
  final messenger = binding.defaultBinaryMessenger;
  final channel = SystemChannels.navigation.name;

  binding.platformDispatcher.defaultRouteNameTestValue = location;
  addTearDown(binding.platformDispatcher.clearDefaultRouteNameTestValue);

  messenger.setMockMessageHandler(channel, (ByteData? message) {
    binding.platformDispatcher.clearDefaultRouteNameTestValue();
    return messenger.delegate.send(channel, message);
  });
  addTearDown(() => messenger.setMockMessageHandler(channel, null));
}

Future<AuthTokenStore> _reloadAt(WidgetTester tester, String location) async {
  tester.view.physicalSize = const Size(1400, 1000);
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.resetPhysicalSize);
  addTearDown(tester.view.resetDevicePixelRatio);

  _loadAt(tester, location);
  // Empty store == fresh page load. Only MSAL's cached session gets us past
  // /login, through the real boot gate.
  final tokens = AuthTokenStore();

  await tester.pumpWidget(
    AnchorDashboard(
      tokens: tokens,
      auth: _CachedSessionAuth(),
      api: _dummyClient(),
      sessions: _FakeSessions(),
      bundles: _FakeBundles(),
      classes: _FakeClasses(),
      apiBaseUrl: Uri.parse('http://localhost'),
    ),
  );
  await tester.pumpAndSettle();
  return tokens;
}

void main() {
  ensureE2eBinding();

  testWidgets(
    'a reload on /classes with a cached session lands back on Classes (#378)',
    (tester) async {
      final tokens = await _reloadAt(tester, '/classes');

      expect(tokens.isAuthenticated, isTrue);
      expect(find.byType(ClassesPage), findsOneWidget);
      expect(find.text('02 · CLASSES'), findsOneWidget);
      expect(find.text('Math 101'), findsWidgets);
      expect(find.byType(HomePage), findsNothing);
      expect(find.byKey(const Key('login-headline')), findsNothing);
      // In a browser, the URL bar still holds the route after the restore.
      if (kIsWeb) expect(Uri.base.fragment, '/classes');
      expect(tester.takeException(), isNull);
    },
  );

  testWidgets('a link to a past session opens that session, not Home (#378)', (
    tester,
  ) async {
    final tokens = await _reloadAt(tester, '/history/s-past');

    expect(tokens.isAuthenticated, isTrue);
    expect(find.byType(PastSessionPage), findsOneWidget);
    expect(find.text('PAST SESSION'), findsOneWidget);
    expect(find.text('Math 101'), findsWidgets);
    expect(find.byType(HomePage), findsNothing);
    if (kIsWeb) expect(Uri.base.fragment, '/history/s-past');
    expect(tester.takeException(), isNull);
  });
}
