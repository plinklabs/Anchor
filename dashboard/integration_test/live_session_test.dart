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
import 'package:plink_design_system/plink_design_system.dart';

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
//   - pushed events render in the live event feed,
//   - the end of the teacher's other session doesn't end this one (#354),
//   - after a hub reconnect the page joins its session again and catches up on
//     what it missed while offline (#365),
//   - while the hub connection is down the page says live updates are paused,
//     and a closed connection can be reconnected from the page (#370).
//
// The fake-auth seam is the documented fallback the issue calls for: a seeded
// AuthTokenStore + a no-op MsalAuthService get us past the /login redirect, and
// `hubClientFactory` injects the stub feed in place of the real SignalR client.

const _sessionId = '11111111-2222-3333-4444-555555555555';
const _otherSessionId = '99999999-8888-7777-6666-555555555555';
final _startedAt = DateTime(2026, 6, 12, 9, 15);

ApiClient _dummyClient() => ApiClient(
  baseUrl: Uri.parse('http://localhost'),
  tokenProvider: () async => null,
);

SessionParticipantInfo _participant(
  String name,
  ParticipantLiveState state, {
  bool tampered = false,
}) => SessionParticipantInfo(
  userId: name,
  displayName: name,
  joinedAt: null,
  declinedAt: null,
  leftAt: null,
  state: state,
  tampered: tampered,
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
  Future<String> acquireTokenSilent() async => 'fake-token';
  @override
  AccountInfo? currentAccount() => null;
}

/// Stands in for the SignalR client. Production builds the real one; here the
/// test pushes events through [emit] to mimic the backend's hub broadcasts.
///
/// It routes them the way the hub does (#365): nothing arrives while the
/// connection is down, and the roster signals go to the session group, so they
/// arrive only while the current connection has joined it. A reconnect is a
/// new connection, in no session group until the page calls JoinSession again.
class _StubHub extends SessionHubClient {
  _StubHub()
    : super(apiBaseUrl: Uri.parse('http://localhost'), tokenProvider: _noToken);

  static Future<String?> _noToken() async => null;

  /// What SessionBroadcaster sends to the session group.
  static const _sessionGroupKinds = {
    'ParticipantStateChanged',
    'HeartbeatLost',
    'AgentReconnected',
    'UnblockRequested',
    'TamperDetected',
  };

  final _ctrl = StreamController<SessionEvent>.broadcast();
  final _reconnectedCtrl = StreamController<void>.broadcast();
  final _linkCtrl = StreamController<SessionHubLinkState>.broadcast();
  bool _online = true;
  bool _inSessionGroup = false;
  int joinCalls = 0;
  int restartCalls = 0;

  @override
  Stream<SessionEvent> get events => _ctrl.stream;

  @override
  Stream<void> get reconnected => _reconnectedCtrl.stream;

  @override
  Stream<SessionHubLinkState> get linkState => _linkCtrl.stream;

  void emit(String kind, [Map<String, dynamic> payload = const {}]) {
    if (!_online) return;
    if (_sessionGroupKinds.contains(kind) && !_inSessionGroup) return;
    _ctrl.add(SessionEvent(kind: kind, payload: payload, at: DateTime.now()));
  }

  /// The connection drops, e.g. a network blip or a backend restart, and
  /// SignalR starts retrying it.
  void drop() {
    _online = false;
    _inSessionGroup = false;
    _linkCtrl.add(SessionHubLinkState.reconnecting);
  }

  /// SignalR's automatic reconnect brings the connection back.
  void reconnect() {
    _online = true;
    _linkCtrl.add(SessionHubLinkState.connected);
    _reconnectedCtrl.add(null);
  }

  /// The connection closes and nothing retries it (#370), e.g. the server
  /// turned it away.
  void close() {
    _online = false;
    _inSessionGroup = false;
    _linkCtrl.add(SessionHubLinkState.disconnected);
  }

  @override
  Future<void> restart() async {
    restartCalls++;
    reconnect();
  }

  @override
  Future<void> connect() async {}
  @override
  Future<void> joinSession(String sessionId, {String? joinCode}) async {
    joinCalls++;
    _inSessionGroup = true;
  }

  @override
  Future<void> leaveSession(String sessionId) async {}
  @override
  Future<void> disconnect() async {}
  @override
  Future<void> dispose() async {
    await _ctrl.close();
    await _reconnectedCtrl.close();
    await _linkCtrl.close();
  }
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
  // How often the page fetched the detail and the pending list (#365).
  int getSessionCalls = 0;
  int unblockRequestsCalls = 0;

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
  Future<SessionDetail> getSession(String sessionId) async {
    getSessionCalls++;
    return SessionDetail(
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
  }

  @override
  Future<List<UnblockRequestSummary>> unblockRequests(String sessionId) async {
    unblockRequestsCalls++;
    return pending;
  }

  @override
  Future<void> approveUnblock(
    String sessionId,
    String userId,
    String host,
  ) async {
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

typedef _Harness = ({
  _StubHub hub,
  _FakeSessions sessions,
  _FakeBundles bundles,
});

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

  testWidgets('a tamper push flags the student on the live roster (#105)', (
    tester,
  ) async {
    final h = await _bootToLiveSession(tester);

    // Ada starts clean — no flag.
    expect(find.byTooltip('Tampering detected'), findsNothing);

    // The student opens InPrivate / downgrades site access; the extension
    // reports it, the backend persists a TamperDetected event and broadcasts.
    // The page re-fetches the detail, whose snapshot now flags Ada.
    h.sessions.roster = [
      _participant('Ada', ParticipantLiveState.joined, tampered: true),
    ];
    h.hub.emit('TamperDetected', {'userId': 'Ada', 'kind': 'inprivate_opened'});
    await tester.pumpAndSettle();

    expect(find.byTooltip('Tampering detected'), findsOneWidget);
    // Ada is still "in session" — tampering is orthogonal to live state.
    expect(find.text('In session'), findsOneWidget);
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

  testWidgets(
    'approving a request for the whole class issues a class grant (#101)',
    (tester) async {
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
    },
  );

  testWidgets(
    'toggling a bundle chip issues PUT /sessions/{id}/bundles (#132)',
    (tester) async {
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
    },
  );

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

  testWidgets(
    "the end of the teacher's other session leaves this live view alone (#354)",
    (tester) async {
      final h = await _bootToLiveSession(tester);

      // The backend sends SessionEnded to the teacher's user group for every
      // session they own, so this page's connection also hears another of the
      // teacher's sessions end — e.g. a forgotten one ended automatically.
      h.hub.emit('SessionEnded', {'sessionId': _otherSessionId});
      await tester.pumpAndSettle();

      expect(find.text('Session ended — event stream stopped.'), findsNothing);
      expect(find.text('LIVE'), findsOneWidget);
      expect(find.text('SessionEnded'), findsNothing);
      expect(find.text('Waiting for events…'), findsOneWidget);

      // Its own end still ends it.
      h.hub.emit('SessionEnded', {'sessionId': _sessionId});
      await tester.pumpAndSettle();

      expect(
        find.text('Session ended — event stream stopped.'),
        findsOneWidget,
      );
      expect(find.text('LIVE'), findsNothing);
      expect(find.text('SessionEnded'), findsOneWidget);
    },
  );

  testWidgets(
    'after a hub reconnect the live view joins its session again and catches up (#365)',
    (tester) async {
      // Room for the pending-requests panel next to the roster.
      tester.view.physicalSize = const Size(1400, 1000);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);

      final h = await _bootToLiveSession(tester);
      expect(h.hub.joinCalls, 1);
      expect(find.text('In session'), findsOneWidget);
      final detailFetches = h.sessions.getSessionCalls;
      final pendingFetches = h.sessions.unblockRequestsCalls;

      // The backend restarts. While the page is offline Ada leaves and asks
      // for a site, and neither broadcast reaches it.
      h.hub.drop();
      h.sessions.roster = [_participant('Ada', ParticipantLiveState.left)];
      h.hub.emit('ParticipantStateChanged', {
        'sessionId': _sessionId,
        'userId': 'Ada',
      });
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
      h.hub.emit('UnblockRequested', {
        'sessionId': _sessionId,
        'host': 'chat.example.com',
      });
      await tester.pumpAndSettle();
      expect(find.text('In session'), findsOneWidget);
      expect(find.text('Pending requests'), findsNothing);

      // The connection comes back: the page joins its session again and
      // re-fetches the roster and the pending requests it missed.
      h.hub.reconnect();
      await tester.pumpAndSettle();

      expect(h.hub.joinCalls, 2);
      expect(h.sessions.getSessionCalls, detailFetches + 1);
      expect(h.sessions.unblockRequestsCalls, pendingFetches + 1);
      expect(find.text('Left'), findsOneWidget);
      expect(find.text('Students (0/1 in session)'), findsOneWidget);
      expect(find.text('Pending requests'), findsOneWidget);
      expect(find.text('chat.example.com'), findsOneWidget);

      // And a roster push after the reconnect reaches it: Ada comes back.
      h.sessions.roster = [_participant('Ada', ParticipantLiveState.joined)];
      h.hub.emit('ParticipantStateChanged', {
        'sessionId': _sessionId,
        'userId': 'Ada',
      });
      await tester.pumpAndSettle();

      expect(find.text('In session'), findsOneWidget);
      expect(find.text('Students (1/1 in session)'), findsOneWidget);
      expect(tester.takeException(), isNull);
    },
  );

  testWidgets(
    'while the hub connection is down the live view says so, and Reconnect catches it up (#370)',
    (tester) async {
      const reconnecting =
          'Connection lost — reconnecting. Live updates are paused.';
      const disconnected = 'Disconnected — live updates are paused.';
      // A realistic window, with room for the notice above the panels.
      tester.view.physicalSize = const Size(1400, 1000);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);

      final h = await _bootToLiveSession(tester);
      expect(find.text(reconnecting), findsNothing);
      expect(find.text(disconnected), findsNothing);

      // The backend goes away: the live view says its updates are paused,
      // while the session itself is still running.
      h.hub.drop();
      await tester.pumpAndSettle();
      expect(find.text(reconnecting), findsOneWidget);
      expect(find.text('LIVE'), findsOneWidget);

      // The connection closes for good. Meanwhile Ada leaves.
      h.hub.close();
      h.sessions.roster = [_participant('Ada', ParticipantLiveState.left)];
      await tester.pumpAndSettle();
      expect(find.text(reconnecting), findsNothing);
      expect(find.text(disconnected), findsOneWidget);
      expect(find.text('In session'), findsOneWidget);

      // The teacher reconnects: the page joins its session again and
      // catches up (#365), and the notice goes.
      await tester.tap(find.widgetWithText(TextButton, 'Reconnect'));
      await tester.pumpAndSettle();

      expect(h.hub.restartCalls, 1);
      expect(h.hub.joinCalls, 2);
      expect(find.text(disconnected), findsNothing);
      expect(find.text('Left'), findsOneWidget);
      expect(find.text('Students (0/1 in session)'), findsOneWidget);

      // And live pushes reach it again: Ada comes back.
      h.sessions.roster = [_participant('Ada', ParticipantLiveState.joined)];
      h.hub.emit('ParticipantStateChanged', {
        'sessionId': _sessionId,
        'userId': 'Ada',
      });
      await tester.pumpAndSettle();
      expect(find.text('In session'), findsOneWidget);
      expect(tester.takeException(), isNull);
    },
  );

  testWidgets('the live view renders as the paper instrument panel (AD4, #169)', (
    tester,
  ) async {
    // A real-app run at a realistic window size: this is the test that would
    // have caught a real-font overflow or a broken composition under the shell
    // chrome that the isolated widget tests structurally miss.
    tester.view.physicalSize = const Size(1400, 1000);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);

    final h = await _bootToLiveSession(tester);

    // Liveness reads as the ping motif + the magenta LIVE spark badge
    // (uppercased by PlinkBadge) — calm, not a pulsing alarm.
    expect(find.byType(Ping), findsWidgets);
    expect(find.text('LIVE'), findsOneWidget);

    // The join code is a mono spec a class can read from across the room.
    expect(find.text('Join code'), findsOneWidget);
    expect(find.text('ABC123'), findsOneWidget);

    // A pushed event lands as a hairline feed row carrying its raw kind label —
    // the instrument read-out (HeartbeatLost is one of the issue's examples).
    h.hub.emit('HeartbeatLost', {'userId': 'Ada'});
    await tester.pumpAndSettle();
    expect(find.text('HeartbeatLost'), findsOneWidget);

    // The composition holds under the real shell + real Fraunces / Space Mono
    // at this window size — no RenderFlex overflow or other exception.
    expect(tester.takeException(), isNull);
  });
}
