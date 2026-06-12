import 'dart:async';

import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/auth_token_store.dart';
import 'package:anchor_dashboard/api/bundles_api.dart';
import 'package:anchor_dashboard/api/classes_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/auth/msal_auth_service.dart';
import 'package:anchor_dashboard/main.dart';
import 'package:anchor_dashboard/realtime/session_hub_client.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';

// Real-app e2e for the teacher dashboard's live-session view (#132).
//
// Unlike the student side (extension #130, agent #131) the dashboard
// authenticates via MSAL and can't be dev-impersonated, and its SignalR feed
// needs a real bearer token — so a real-backend run is infeasible here. Instead
// this boots the *real* AnchorDashboard app (real router, real navigation, real
// layout, real fonts under `flutter drive`) wired to fake API subclasses and a
// stubbed live feed, then drives it the way the backend's SignalR pushes would:
//   - a roster state transition (#100) updates the live roster,
//   - an unblock-request push (#…) surfaces the pending panel,
//   - a UI bundle toggle issues PUT /sessions/{id}/bundles (#93),
//   - pushed events render in the live event feed.
//
// The fake-auth seam is the documented fallback the issue calls for: a seeded
// AuthTokenStore + a no-op MsalAuthService get us past the /login redirect, and
// `hubClientFactory` injects the stub feed in place of the real SignalR client.

const _sessionId = '11111111-2222-3333-4444-555555555555';
final _startedAt = DateTime(2026, 6, 12, 9, 15);

ApiClient _dummyClient() => ApiClient(
  baseUrl: Uri.parse('http://localhost'),
  tokenProvider: () async => null,
);

SessionParticipantInfo _participant(String name, ParticipantLiveState state) =>
    SessionParticipantInfo(
      userId: name,
      displayName: name,
      joinedAt: null,
      declinedAt: null,
      leftAt: null,
      state: state,
    );

/// Past the /login redirect without MSAL: the router only checks
/// `tokens.isAuthenticated`, and nothing in the faked flow calls back into the
/// auth service, so a no-op implementation is enough.
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

/// Stands in for the SignalR client. Production builds the real one; here the
/// test pushes events through [emit] to mimic the backend's hub broadcasts.
class _StubHub extends SessionHubClient {
  _StubHub()
    : super(apiBaseUrl: Uri.parse('http://localhost'), tokenProvider: _noToken);

  static Future<String?> _noToken() async => null;

  final _ctrl = StreamController<SessionEvent>.broadcast();

  @override
  Stream<SessionEvent> get events => _ctrl.stream;

  void emit(String kind, [Map<String, dynamic> payload = const {}]) =>
      _ctrl.add(SessionEvent(kind: kind, payload: payload, at: DateTime.now()));

  @override
  Future<void> connect() async {}
  @override
  Future<void> joinSession(String sessionId, {String? joinCode}) async {}
  @override
  Future<void> leaveSession(String sessionId) async {}
  @override
  Future<void> disconnect() async {}
  @override
  Future<void> dispose() async => _ctrl.close();
}

class _FakeSessions extends SessionsApi {
  _FakeSessions() : super(_dummyClient());

  // Mutable so a test can change server state, then push the event that makes
  // the page re-fetch it — exactly how the live view reacts to SignalR.
  List<SessionParticipantInfo> roster = [
    _participant('Ada', ParticipantLiveState.joined),
  ];
  List<UnblockRequestSummary> pending = const [];
  List<SessionBundleInfo> sessionBundles = const [];
  final List<List<String>> updateBundlesCalls = [];
  // Records approval calls so a test can assert which scope the UI chose (#101).
  final List<(String, String)> perStudentApprovals = [];
  final List<String> classApprovals = [];

  @override
  Future<MeResponse> me() async =>
      MeResponse(id: 't1', displayName: 'Teacher', role: 'Teacher');

  @override
  Future<List<ClassSummary>> classes() async => [
    ClassSummary(id: 'c1', name: 'Math 101', schoolYear: '2025-2026'),
  ];

  @override
  Future<List<ActiveSession>> activeSessions() async => const [];

  @override
  Future<StartSessionResponse> startSession(
    String classId, {
    List<String> bundleIds = const <String>[],
  }) async => StartSessionResponse(
    id: _sessionId,
    classId: classId,
    joinCode: 'ABC123',
    startedAt: _startedAt,
  );

  @override
  Future<SessionDetail> getSession(String sessionId) async => SessionDetail(
    id: sessionId,
    classId: 'c1',
    className: 'Math 101',
    joinCode: 'ABC123',
    startedAt: _startedAt,
    endedAt: null,
    summaries: const [],
    recentEvents: const [],
    participants: roster,
    bundles: sessionBundles,
    grants: const [],
  );

  @override
  Future<List<UnblockRequestSummary>> unblockRequests(String sessionId) async =>
      pending;

  @override
  Future<void> approveUnblock(String sessionId, String userId, String host) async {
    perStudentApprovals.add((userId, host));
  }

  @override
  Future<void> approveUnblockForClass(String sessionId, String host) async {
    classApprovals.add(host);
    // The host is now open for the whole class, so it drops off the pending
    // list — mirror that so the panel updates the way the backend would drive it.
    pending = const [];
  }

  @override
  Future<List<SessionBundleInfo>> updateBundles(
    String sessionId,
    List<String> bundleIds,
  ) async {
    updateBundlesCalls.add(bundleIds);
    sessionBundles = [
      for (final id in bundleIds) SessionBundleInfo(id: id, name: id),
    ];
    return sessionBundles;
  }
}

class _FakeBundles extends BundlesApi {
  _FakeBundles() : super(_dummyClient());

  @override
  Future<List<BundleSummary>> list({bool includeArchived = false}) async => [
    BundleSummary(
      id: 'b1',
      name: 'Math',
      version: 1,
      isArchived: false,
      hasBeenUsed: false,
    ),
  ];
}

typedef _Harness = ({_StubHub hub, _FakeSessions sessions, _FakeBundles bundles});

/// Boots the real app authenticated, then drives the real Home → Start-session
/// navigation to land on the live session view. Returns the fakes so the test
/// can push events and inspect recorded calls.
Future<_Harness> _bootToLiveSession(WidgetTester tester) async {
  final hub = _StubHub();
  final sessions = _FakeSessions();
  final bundles = _FakeBundles();
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
      sessions: sessions,
      bundles: bundles,
      classes: ClassesApi(_dummyClient()),
      apiBaseUrl: Uri.parse('http://localhost'),
      hubClientFactory: ({required apiBaseUrl, required tokenProvider}) => hub,
    ),
  );
  await tester.pumpAndSettle();

  // Real navigation: the teacher starts a session from the home screen, which
  // pushes /session/:id — the flow a widget test of SessionPage can't exercise.
  final startButton = find.textContaining('Start session');
  expect(startButton, findsOneWidget, reason: 'home screen should offer Start');
  await tester.tap(startButton);
  await tester.pumpAndSettle();

  // Sanity: we are now on the live session view.
  expect(find.text('Allowed bundles'), findsOneWidget);

  return (hub: hub, sessions: sessions, bundles: bundles);
}

void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  testWidgets('a SignalR roster transition updates the live roster (#132)', (
    tester,
  ) async {
    final h = await _bootToLiveSession(tester);

    // Ada starts in-session.
    expect(find.text('In session'), findsOneWidget);
    expect(find.text('Students (1/1 in session)'), findsOneWidget);

    // The backend marks Ada's agent stale and broadcasts the transition; the
    // page re-fetches the detail and re-renders the roster.
    h.sessions.roster = [
      _participant('Ada', ParticipantLiveState.heartbeatStale),
    ];
    h.hub.emit('ParticipantStateChanged', {'userId': 'Ada'});
    await tester.pumpAndSettle();

    expect(find.text('Agent stopped reporting'), findsOneWidget);
    expect(find.text('In session'), findsNothing);
    expect(find.text('Students (0/1 in session)'), findsOneWidget);
  });

  testWidgets('an unblock-request push surfaces the pending panel (#132)', (
    tester,
  ) async {
    final h = await _bootToLiveSession(tester);

    expect(find.text('Pending requests'), findsNothing);

    // A student clicks "Request access"; the backend pushes UnblockRequested and
    // the page re-fetches the (now non-empty) pending list.
    final now = DateTime(2026, 6, 12, 9, 20);
    h.sessions.pending = [
      UnblockRequestSummary(
        host: 'chat.example.com',
        count: 1,
        firstRequestedAt: now,
        latestRequestedAt: now,
        requesters: [
          UnblockRequestRequester(
            userId: 'Ada',
            displayName: 'Ada',
            requestedAt: now,
          ),
        ],
      ),
    ];
    h.hub.emit('UnblockRequested', {'host': 'chat.example.com'});
    await tester.pumpAndSettle();

    expect(find.text('Pending requests'), findsOneWidget);
    expect(find.text('chat.example.com'), findsOneWidget);
    // The Approve label only renders on the pending row's button — a plain text
    // finder keeps this stable across Flutter versions (the `*.tonalIcon`
    // button isn't reliably typed as a `FilledButton` ancestor on all of them).
    expect(find.text('Approve'), findsOneWidget);
  });

  testWidgets('approving a request for the whole class issues a class grant (#101)', (
    tester,
  ) async {
    final h = await _bootToLiveSession(tester);

    final now = DateTime(2026, 6, 12, 9, 20);
    h.sessions.pending = [
      UnblockRequestSummary(
        host: 'chat.example.com',
        count: 1,
        firstRequestedAt: now,
        latestRequestedAt: now,
        requesters: [
          UnblockRequestRequester(
            userId: 'Ada',
            displayName: 'Ada',
            requestedAt: now,
          ),
        ],
      ),
    ];
    h.hub.emit('UnblockRequested', {'host': 'chat.example.com'});
    await tester.pumpAndSettle();
    expect(find.text('chat.example.com'), findsOneWidget);

    // The whole-class scope is behind the kebab — the safer per-student action
    // stays the primary button (#101).
    await tester.tap(find.byIcon(Icons.more_vert));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Approve for whole class'));
    await tester.pumpAndSettle();

    // The UI chose the class scope, not a per-student grant, and the now-granted
    // host has dropped off the pending panel.
    expect(h.sessions.classApprovals, ['chat.example.com']);
    expect(h.sessions.perStudentApprovals, isEmpty);
    expect(find.text('Pending requests'), findsNothing);
  });

  testWidgets('toggling a bundle chip issues PUT /sessions/{id}/bundles (#132)', (
    tester,
  ) async {
    final h = await _bootToLiveSession(tester);

    final chip = find.widgetWithText(FilterChip, 'Math');
    expect(chip, findsOneWidget);

    await tester.tap(chip);
    await tester.pumpAndSettle();

    // The UI toggle drove SessionsApi.updateBundles, which is the PUT call.
    expect(h.sessions.updateBundlesCalls, [
      ['b1'],
    ]);
    // And the chip reflects the new source-of-truth selection.
    final selected = tester.widget<FilterChip>(chip);
    expect(selected.selected, isTrue);
  });

  testWidgets('pushed events render in the live event feed (#132)', (
    tester,
  ) async {
    final h = await _bootToLiveSession(tester);

    // Empty feed shows the placeholder until the first event arrives.
    expect(find.text('Waiting for events…'), findsOneWidget);

    h.hub.emit('SessionStarted', {'sessionId': _sessionId});
    await tester.pumpAndSettle();

    expect(find.text('Waiting for events…'), findsNothing);
    expect(find.text('SessionStarted'), findsOneWidget);
  });
}
