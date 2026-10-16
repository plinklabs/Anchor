import 'dart:async';
import 'dart:math' as math;

import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/auth_token_store.dart';
import 'package:anchor_dashboard/api/bundles_api.dart';
import 'package:anchor_dashboard/api/classes_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/auth/msal_auth_service.dart';
import 'package:anchor_dashboard/main.dart';
import 'package:anchor_dashboard/realtime/session_hub_client.dart';
import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:plink_design_system/plink_design_system.dart';

import 'support/e2e_binding.dart';

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
//     and a closed connection can be reconnected from the page (#370),
//   - a failed join, approval, bundle change or End reads as a sentence, never
//     the raw exception (#383),
//   - the join code says how the teacher takes part in the session themselves
//     (#410),
//   - on a short window the page scrolls to every panel and the feed, and the
//     join code sits in the header row, dropping under the title on a narrow
//     one (#422).
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

  /// What JoinSession throws, e.g. the hub refusing it with a HubException
  /// (#383).
  Object? joinError;

  @override
  Future<void> connect() async {}
  @override
  Future<void> joinSession(String sessionId, {String? joinCode}) async {
    joinCalls++;
    final error = joinError;
    if (error != null) throw error;
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
  // What the teacher's own actions throw, when a test sets it (#383).
  Object? endError;
  Object? approveError;
  Object? updateBundlesError;

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
  Future<void> endSession(String sessionId) async {
    final error = endError;
    if (error != null) throw error;
  }

  @override
  Future<void> approveUnblock(
    String sessionId,
    String userId,
    String host,
  ) async {
    final error = approveError;
    if (error != null) throw error;
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
    final error = updateBundlesError;
    if (error != null) throw error;
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
/// can push events and inspect recorded calls. [joinError] is what the hub's
/// JoinSession throws when the page opens; [roster] replaces the one-student
/// class.
Future<_Harness> _bootToLiveSession(
  WidgetTester tester, {
  Object? joinError,
  List<SessionParticipantInfo>? roster,
}) async {
  final hub = _StubHub()..joinError = joinError;
  final sessions = _FakeSessions();
  if (roster != null) sessions.roster = roster;
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
  ensureE2eBinding();

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

  testWidgets(
    'the join code says how the teacher takes part themselves (#410)',
    (tester) async {
      // The teacher isn't held to the session they start; taking part is their
      // choice, with this code, from the agent. The hint sits under the code
      // in the real composition, beside the copy button, with the real fonts.
      const hint = "Taking part yourself? Use 'Join by code' in the agent.";
      await _bootToLiveSession(tester);

      expect(find.text('ABC123'), findsOneWidget);
      expect(find.text(hint), findsOneWidget);
      final code = tester.getRect(find.text('ABC123'));
      final line = tester.getRect(find.text(hint));
      final copy = tester.getRect(find.byTooltip('Copy code'));
      expect(line.top, greaterThan(code.bottom));
      expect(line.left, code.left);
      expect(line.right, lessThanOrEqualTo(copy.left));
      // Above the panels that follow the join code.
      expect(
        line.bottom,
        lessThan(tester.getTopLeft(find.text('Students (1/1 in session)')).dy),
      );
      expect(tester.takeException(), isNull);
    },
  );

  group('a failed action reads as a sentence, never the raw exception', () {
    void bigWindow(WidgetTester tester) {
      // Room for the pending-requests and bundles panels under the roster.
      tester.view.physicalSize = const Size(1400, 1000);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);
    }

    void expectNoRawError() {
      for (final raw in const [
        'ApiException',
        'HubException',
        'Exception',
        'Failed to fetch',
        'JoinSession',
        'System.',
      ]) {
        expect(find.textContaining(raw), findsNothing, reason: raw);
      }
    }

    testWidgets('the hub refusing JoinSession (#383)', (tester) async {
      bigWindow(tester);
      await _bootToLiveSession(
        tester,
        // What signalr_core throws for a HubException from the hub.
        joinError: Exception(
          "An unexpected error occurred invoking 'JoinSession' on the "
          'server. HubException: Session not found or already ended.',
        ),
      );

      expect(
        find.text(
          'Could not connect to the live feed for this session. Reload the '
          'page to try again.',
        ),
        findsOneWidget,
      );
      expectNoRawError();
      expect(find.text('LIVE'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets('approving, changing bundles and ending fail in turn (#383)', (
      tester,
    ) async {
      bigWindow(tester);
      final h = await _bootToLiveSession(tester);
      final offline = http.ClientException(
        'Failed to fetch',
        Uri.parse('http://localhost/sessions/$_sessionId/unblock'),
      );

      // A student asks for a site; approving it fails while offline.
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
      h.sessions.approveError = offline;
      await tester.tap(find.widgetWithText(OutlinedButton, 'Approve'));
      await tester.pumpAndSettle();

      expect(
        find.text('Could not approve the request. Please try again.'),
        findsOneWidget,
      );
      expect(find.text('chat.example.com'), findsOneWidget);
      expectNoRawError();

      // Changing the allowed bundles answers 500.
      h.sessions.updateBundlesError = ApiException(
        500,
        'System.InvalidOperationException: boom',
      );
      await tester.tap(find.widgetWithText(FilterChip, 'Math'));
      await tester.pumpAndSettle();

      expect(
        find.text('Could not update the allowed bundles. Please try again.'),
        findsOneWidget,
      );
      expectNoRawError();

      // Ending the session answers 500; it's still live.
      h.sessions.endError = ApiException(500, 'System.Exception: boom');
      await tester.tap(find.widgetWithText(OutlinedButton, 'End session'));
      await tester.pumpAndSettle();

      expect(
        find.text('Failed to end session. Please try again.'),
        findsOneWidget,
      );
      expect(find.text('LIVE'), findsOneWidget);
      expectNoRawError();
      expect(tester.takeException(), isNull);
    });
  });

  group('the live view on a short, a tall and a narrow window (#422)', () {
    void window(WidgetTester tester, Size size) {
      tester.view.physicalSize = size;
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);
    }

    // A laptop at 150% with the browser's chrome and the taskbar: about
    // 1280x600 logical px is left for the page, as the issue reports.
    const short = Size(1280, 600);

    final sixStudents = [
      for (final name in ['Ada', 'Bo', 'Cas', 'Dina', 'Eli', 'Fien'])
        _participant(name, ParticipantLiveState.joined),
    ];

    /// The part of the window the page scrolls in, under the shell's chrome.
    Rect page(WidgetTester tester) =>
        tester.getRect(find.byType(CustomScrollView));

    bool inView(WidgetTester tester, Finder finder) {
      final rect = tester.getRect(finder);
      return rect.top >= page(tester).top && rect.bottom <= page(tester).bottom;
    }

    /// Turns the mouse wheel by [dy] over the page's left gutter, the way a
    /// teacher scrolls. The roster's list sits inside the gutter, and the feed
    /// holds too few events here to scroll on its own, so the page scrolls.
    Future<void> wheel(WidgetTester tester, double dy) async {
      final pointer = TestPointer(1, PointerDeviceKind.mouse)
        ..hover(page(tester).bottomLeft + const Offset(PlinkSpacing.s3, -40));
      await tester.sendEventToBinding(pointer.scroll(Offset(0, dy)));
      await tester.pumpAndSettle();
    }

    /// Wheels down a notch at a time until [finder] is in view, or gives up.
    Future<void> wheelUntilInView(WidgetTester tester, Finder finder) async {
      for (var i = 0; i < 30 && !inView(tester, finder); i++) {
        await wheel(tester, 80);
      }
    }

    testWidgets(
      'every panel and the feed can be scrolled to on a short window',
      (tester) async {
        window(tester, short);
        final h = await _bootToLiveSession(tester, roster: sixStudents);
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

        // The panels stack to more than the window holds, with the real
        // fonts. Nothing overflows: the page scrolls instead.
        expect(find.text('Pending requests'), findsOneWidget);
        expect(find.text('Students (6/6 in session)'), findsOneWidget);
        expect(tester.takeException(), isNull);
        final chip = find.widgetWithText(FilterChip, 'Math');
        expect(inView(tester, chip), isFalse, reason: 'below the window');

        // The teacher scrolls down to the allowed bundles and toggles one.
        await wheelUntilInView(tester, chip);
        expect(inView(tester, chip), isTrue);
        await tester.tap(chip);
        await tester.pumpAndSettle();
        expect(h.sessions.updateBundlesCalls, [
          ['b1'],
        ]);
        expect(tester.widget<FilterChip>(chip).selected, isTrue);

        // Further down, the feed: an event lands in it, in view.
        h.hub.emit('HeartbeatLost', {'userId': 'Ada'});
        await tester.pumpAndSettle();
        // Not skipping offstage: while the feed is still wholly below the
        // window the finder would otherwise not see its rows at all.
        final event = find.text('HeartbeatLost', skipOffstage: false);
        expect(event, findsOneWidget);
        await wheelUntilInView(tester, event);
        expect(inView(tester, event), isTrue);
        // At the end of the page the feed has room for several more rows
        // under its label, not the sliver the panels used to leave it.
        await wheel(tester, 5000);
        final activity = find.text('Activity');
        expect(inView(tester, activity), isTrue);
        expect(
          page(tester).bottom - tester.getRect(activity).bottom,
          greaterThanOrEqualTo(300),
        );

        // The ended view holds at this height too.
        h.hub.emit('SessionEnded', {'sessionId': _sessionId});
        await tester.pumpAndSettle();
        expect(
          find.text('Session ended — event stream stopped.'),
          findsOneWidget,
        );
        expect(find.text('ABC123'), findsNothing);
        expect(tester.takeException(), isNull);
      },
    );

    testWidgets(
      'on a tall window the feed still fills the rest, with nothing to scroll',
      (tester) async {
        // The default 1400x1000 window: the panels leave the feed more than
        // its least height, so it takes all of it, as before #422.
        await _bootToLiveSession(tester);

        final feed = tester.getRect(
          find
              .ancestor(
                of: find.text('Waiting for events…'),
                matching: find.byType(SizedBox),
              )
              .first,
        );
        expect(feed.bottom, page(tester).bottom);
        expect(feed.height, greaterThan(320));
        final pageScroll = tester
            .state<ScrollableState>(
              find
                  .descendant(
                    of: find.byType(CustomScrollView),
                    matching: find.byType(Scrollable),
                  )
                  .first,
            )
            .position;
        expect(pageScroll.maxScrollExtent, 0);
        expect(tester.takeException(), isNull);
      },
    );

    testWidgets(
      'the join code sits in the header row, between the class and End session',
      (tester) async {
        window(tester, short);
        await _bootToLiveSession(tester);

        final label = tester.getRect(find.text('Join code'));
        final code = tester.getRect(find.text('ABC123'));
        // The whole button, tap-target padding and all: the code's block ends
        // there.
        final copy = tester.getRect(
          find.widgetWithIcon(IconButton, Icons.copy),
        );
        final spec = tester.getRect(find.text('Session 2026-06-12 09:15'));
        final headline = tester.getRect(find.text('Math 101'));
        final end = tester.getRect(
          find.widgetWithText(OutlinedButton, 'End session'),
        );

        // Level with the class name, not a band of its own under the header.
        expect(label.top, lessThan(headline.bottom));
        expect(code.top, lessThan(headline.bottom));
        expect(code.left, label.left);
        // Between the class and End session, and centred between them.
        final classRight = math.max(spec.right, headline.right);
        expect(code.left, greaterThan(classRight));
        expect(copy.right, lessThanOrEqualTo(end.left));
        expect(
          (code.left + copy.right) / 2,
          moreOrLessEquals((classRight + end.left) / 2, epsilon: 1),
        );
        // The panels start under the header row.
        expect(
          tester.getTopLeft(find.text('Students (1/1 in session)')).dy,
          greaterThan(copy.bottom),
        );

        expect(tester.takeException(), isNull);
      },
    );

    testWidgets(
      'on a narrow window the join code drops under the title instead of overflowing',
      (tester) async {
        window(tester, const Size(800, 800));
        await _bootToLiveSession(tester);

        final code = tester.getRect(find.text('ABC123'));
        final label = tester.getRect(find.text('Join code'));
        final copy = tester.getRect(find.byTooltip('Copy code'));
        final headline = tester.getRect(find.text('Math 101'));
        final end = tester.getRect(
          find.widgetWithText(OutlinedButton, 'End session'),
        );

        // Under the title, flush left with it; End session stays up top.
        expect(label.top, greaterThan(headline.bottom));
        expect(code.left, headline.left);
        expect(end.top, lessThan(headline.bottom));
        expect(copy.right, lessThanOrEqualTo(end.left));
        expect(tester.takeException(), isNull);
      },
    );
  });
}
