import 'dart:async';
import 'dart:math' as math;

import 'package:anchor_dashboard/alerts/alert_platform.dart';
import 'package:anchor_dashboard/alerts/live_alert_services.dart';
import 'package:anchor_dashboard/alerts/live_alert_settings.dart';
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
//   - pushed events render in the live activity feed as plain sentences,
//     only what needs the teacher until they show all activity, which the
//     browser remembers (#491),
//   - a running session opened again shows what happened in it so far, then
//     continues live, each event once (#492),
//   - the end of the teacher's other session doesn't end this one (#354),
//   - after a hub reconnect the page joins its session again and catches up on
//     what it missed while offline (#365), its feed too, each event in its
//     place in time (#499),
//   - while the hub connection is down the page says live updates are paused,
//     and a closed connection can be reconnected from the page (#370),
//   - a failed join, approval, bundle change or End reads as a sentence, never
//     the raw exception (#383),
//   - the join code says how the teacher takes part in the session themselves
//     (#410),
//   - a student whose agent never connected (only the extension joined) shows
//     on the live roster, explained, until the agent turns up (#444),
//   - a class member whose laptop never joined shows on the live roster and
//     in the feed, explained, also when the page is opened again, until they
//     join (#493),
//   - the teacher picks when the session ends on Home, and the live view shows
//     that planned end while still offering End session (#447),
//   - on a short window the page scrolls to every panel and the feed, and the
//     join code sits in the header row, dropping under the title on a narrow
//     one (#422),
//   - on a wide window the students fill a column on the left, the whole
//     class at once, and the requests, the bundles and the feed share the
//     right, each column scrolling on its own; a narrower window stacks the
//     panels as before (#494).
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
  // When a session of the class can end (#447): no lesson period unless a
  // test gives some, so Home offers a time. How the session was asked to end,
  // and the planned end the detail reports.
  SessionEndOptions endOptionsValue = SessionEndOptions();
  SessionEnd? startedEnd;
  DateTime? plannedEnd;
  int endSessionCalls = 0;
  // What the teacher's own actions throw, when a test sets it (#383).
  Object? endError;
  Object? approveError;
  Object? updateBundlesError;
  // The teacher's running sessions, which Home offers to resume (#126).
  List<ActiveSession> active = const [];
  // What the backend stored for the session, newest first, which the page
  // loads its feed's history from (#492); how often it did; and what happens
  // while it does, e.g. an event broadcast between its join and the load.
  List<SessionRecentEvent> stored = const [];
  int sessionEventsCalls = 0;
  void Function()? whileLoadingEvents;

  @override
  Future<MeResponse> me() async =>
      MeResponse(id: 't1', displayName: 'Teacher', role: 'Teacher');

  @override
  Future<List<ClassSummary>> classes() async => [
    ClassSummary(id: 'c1', name: 'Math 101', schoolYear: '2025-2026'),
  ];

  @override
  Future<List<ActiveSession>> activeSessions() async => active;

  @override
  Future<SessionEndOptions> endOptions(String classId) async => endOptionsValue;

  @override
  Future<StartSessionResponse> startSession(
    String classId, {
    List<String> bundleIds = const <String>[],
    SessionEnd? end,
  }) async {
    startedEnd = end;
    return StartSessionResponse(
      id: _sessionId,
      classId: classId,
      joinCode: 'ABC123',
      startedAt: _startedAt,
    );
  }

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
      endsAt: plannedEnd,
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
  Future<List<SessionRecentEvent>> sessionEvents(
    String sessionId, {
    Iterable<String> kinds = const <String>[],
    int limit = 200,
  }) async {
    sessionEventsCalls++;
    whileLoadingEvents?.call();
    return stored;
  }

  @override
  Future<void> endSession(String sessionId) async {
    endSessionCalls++;
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

/// The colour of the status dot on the activity feed's row that reads
/// [sentence].
Color? _feedDotColor(WidgetTester tester, String sentence) {
  final row = find
      .ancestor(of: find.text(sentence), matching: find.byType(Row))
      .first;
  final dot = find.descendant(
    of: row,
    matching: find.byWidgetPredicate(
      (w) =>
          w is Container &&
          w.decoration is BoxDecoration &&
          (w.decoration! as BoxDecoration).shape == BoxShape.circle,
    ),
  );
  return (tester.widget<Container>(dot).decoration! as BoxDecoration).color;
}

typedef _Harness = ({
  _StubHub hub,
  List<_StubHub> hubs,
  _FakeSessions sessions,
  _FakeBundles bundles,
});

/// Boots the real app authenticated, then drives the real Home → Start-session
/// navigation to land on the live session view. Returns the fakes so the test
/// can push events and inspect recorded calls: [_Harness.hub] is the first
/// live page's feed, and each live page the test opens after it gets its own,
/// in [_Harness.hubs], as production builds one per page. [joinError] is what the hub's
/// JoinSession throws when the page opens; [roster] replaces the one-student
/// class. [endOptions] is what Home offers for the session's end, and
/// [plannedEnd] the end the detail then reports (#447); [beforeStart] makes the
/// teacher's choices on Home before Start.
///
/// The app keeps the teacher's alert settings and the activity feed's switch
/// in [settings]: the browser's localStorage, as in production, in a browser
/// (memory in a VM copy). It outlives a test, so a boot starts from none
/// unless the test passes the store it kept. A [reload] boots a new app, as
/// after the browser reloads the page: nothing carries over but the store.
Future<_Harness> _bootToLiveSession(
  WidgetTester tester, {
  Object? joinError,
  List<SessionParticipantInfo>? roster,
  SessionEndOptions? endOptions,
  DateTime? plannedEnd,
  Future<void> Function()? beforeStart,
  AlertSettingsStore? settings,
  bool reload = false,
}) async {
  if (reload) await tester.pumpWidget(const SizedBox.shrink());
  final store = settings ?? (createAlertSettingsStore()..write('{}'));
  final hubs = <_StubHub>[];
  _StubHub newHub() {
    final hub = _StubHub()..joinError = joinError;
    hubs.add(hub);
    return hub;
  }

  final sessions = _FakeSessions()..plannedEnd = plannedEnd;
  if (roster != null) sessions.roster = roster;
  if (endOptions != null) sessions.endOptionsValue = endOptions;
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
      hubClientFactory: ({required apiBaseUrl, required tokenProvider}) =>
          newHub(),
      // The browser's own, as production builds them, on [store].
      alertServices: LiveAlertServices(
        settings: LiveAlertSettings(store: store),
        sound: createAlertSound(),
      ),
    ),
  );
  await tester.pumpAndSettle();

  // Real navigation: the teacher starts a session from the home screen, which
  // pushes /session/:id — the flow a widget test of SessionPage can't exercise.
  final startButton = find.textContaining('Start session');
  expect(startButton, findsOneWidget, reason: 'home screen should offer Start');
  await beforeStart?.call();
  await tester.tap(startButton);
  await tester.pumpAndSettle();

  // Sanity: we are now on the live session view.
  expect(find.text('Allowed bundles'), findsOneWidget);

  return (hub: hubs.first, hubs: hubs, sessions: sessions, bundles: bundles);
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

  testWidgets(
    'the teacher ends the session at the end of the next period, and the live '
    'view shows when it ends (#447)',
    (tester) async {
      final h = await _bootToLiveSession(
        tester,
        endOptions: SessionEndOptions(
          thisPeriodEndsAt: DateTime(2026, 6, 12, 10, 5),
          nextPeriodEndsAt: DateTime(2026, 6, 12, 10, 55),
        ),
        plannedEnd: DateTime(2026, 6, 12, 10, 55),
        beforeStart: () async {
          // Home offers the end of this period first, then the next one.
          expect(
            find.text('At the end of this period (10:05)'),
            findsOneWidget,
          );
          await tester.tap(find.byKey(const Key('end-picker')));
          await tester.pumpAndSettle();
          await tester.tap(
            find.text('At the end of the next period (10:55)').last,
          );
          await tester.pumpAndSettle();
          expect(
            find.text('At the end of the next period (10:55)'),
            findsOneWidget,
          );
        },
      );

      expect(h.sessions.startedEnd?.mode, 'NextPeriod');
      expect(
        find.text('Session 2026-06-12 09:15 · ends 10:55'),
        findsOneWidget,
      );

      // The teacher can still end it earlier.
      await tester.tap(find.widgetWithText(OutlinedButton, 'End session'));
      await tester.pumpAndSettle();
      expect(h.sessions.endSessionCalls, 1);
      expect(tester.takeException(), isNull);
    },
  );

  testWidgets(
    'without a lesson period the teacher types when the session ends (#447)',
    (tester) async {
      final h = await _bootToLiveSession(
        tester,
        beforeStart: () async {
          expect(find.text('At a time I pick'), findsOneWidget);
          await tester.enterText(find.byKey(const Key('end-time')), '23:59');
          await tester.pumpAndSettle();
        },
      );

      final end = h.sessions.startedEnd!;
      expect(end.mode, 'SpecificTime');
      expect(end.at!.hour, 23);
      expect(end.at!.minute, 59);
      expect(end.at!.isAfter(DateTime.now()), isTrue);
      expect(tester.takeException(), isNull);
    },
  );

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

  testWidgets(
    'a student whose agent never connects shows on the live roster until it '
    'turns up (#444)',
    (tester) async {
      final h = await _bootToLiveSession(
        tester,
        roster: [
          _participant('Ada', ParticipantLiveState.joined),
          _participant('Bo', ParticipantLiveState.joined),
        ],
      );
      expect(find.text('Students (2/2 in session)'), findsOneWidget);

      // Only Bo's extension joined. Once the join grace passes without an
      // agent heartbeat, the backend's monitor pushes the roster state change;
      // the page re-fetches the roster, whose snapshot now says so.
      h.sessions.roster = [
        _participant('Ada', ParticipantLiveState.joined),
        _participant('Bo', ParticipantLiveState.agentMissing),
      ];
      h.hub.emit('ParticipantStateChanged', {
        'userId': 'Bo',
        'displayName': 'Bo',
        'state': 'AgentMissing',
      });
      await tester.pumpAndSettle();

      expect(find.text('Agent not connected'), findsOneWidget);
      expect(find.text('Students (1/2 in session)'), findsOneWidget);
      expect(
        tester.getTopLeft(find.text('Bo')).dy,
        lessThan(tester.getTopLeft(find.text('Ada')).dy),
      );
      final error = Theme.of(tester.element(find.text('Bo'))).colorScheme.error;
      // The feed says so, with the stronger marker (#491).
      expect(_feedDotColor(tester, "Bo's agent hasn't reported in."), error);

      // Hovering the state says what it means for the class.
      final mouse = await tester.createGesture(kind: PointerDeviceKind.mouse);
      await mouse.addPointer(location: Offset.zero);
      addTearDown(mouse.removePointer);
      await mouse.moveTo(tester.getCenter(find.text('Agent not connected')));
      await tester.pumpAndSettle();
      expect(
        find.text(
          "Only the browser extension joined. Apps aren't being blocked on "
          'this laptop.',
        ),
        findsOneWidget,
      );
      await mouse.moveTo(Offset.zero);
      await tester.pumpAndSettle();

      // Bo's agent starts after all: it joins (a Joined push) and its first
      // heartbeat clears the report (AgentReconnected).
      h.hub.emit('ParticipantStateChanged', {
        'userId': 'Bo',
        'displayName': 'Bo',
        'state': 'Joined',
      });
      h.sessions.roster = [
        _participant('Ada', ParticipantLiveState.joined),
        _participant('Bo', ParticipantLiveState.joined),
      ];
      h.hub.emit('AgentReconnected', {'userId': 'Bo'});
      await tester.pumpAndSettle();

      expect(find.text('Agent not connected'), findsNothing);
      expect(find.text('Students (2/2 in session)'), findsOneWidget);
      // An ordinary join is no cause for attention: the feed shows it only
      // with all activity, without the marker.
      expect(find.text('Bo joined.'), findsNothing);
      await tester.tap(find.byKey(const Key('feed-show-all')));
      await tester.pumpAndSettle();
      expect(_feedDotColor(tester, 'Bo joined.'), isNot(error));
      expect(_feedDotColor(tester, 'Bo is connected again.'), isNot(error));
      expect(tester.takeException(), isNull);
    },
  );

  testWidgets(
    'a class member whose laptop never joins shows on the live roster and in '
    'the feed, also after the page opens again, until they join (#493)',
    (tester) async {
      // When the monitor noticed, 30 s after the start, and stored it.
      const reportedAt = '2026-06-12T07:15:31.0000000+00:00';
      SessionParticipantInfo student(String name, ParticipantLiveState state) =>
          SessionParticipantInfo(
            userId: name,
            displayName: name,
            joinedAt: state == ParticipantLiveState.joined
                ? DateTime.parse('2026-06-12T07:15:06+00:00')
                : null,
            declinedAt: null,
            leftAt: null,
            state: state,
          );
      const notJoinedYet = "Bo hasn't joined yet.";
      const tooltip =
          "This laptop hasn't joined the session. Nothing is being blocked "
          "for this student, or they're absent.";

      final h = await _bootToLiveSession(
        tester,
        roster: [
          student('Ada', ParticipantLiveState.joined),
          student('Bo', ParticipantLiveState.neverJoined),
        ],
      );
      // Right after the start Bo's laptop may still be on its way: calm.
      expect(find.text('Not joined'), findsOneWidget);
      expect(find.text('Students (1/2 in session)'), findsOneWidget);
      final error = Theme.of(tester.element(find.text('Bo'))).colorScheme.error;
      expect(
        tester.widget<Text>(find.text('Not joined')).style?.color,
        isNot(error),
      );

      // The join grace passes without Bo's laptop joining: the backend's
      // monitor pushes the state change, and the page re-fetches the roster,
      // whose snapshot now says so.
      h.sessions.roster = [
        student('Ada', ParticipantLiveState.joined),
        student('Bo', ParticipantLiveState.notConnected),
      ];
      h.hub.emit('ParticipantStateChanged', {
        'sessionId': _sessionId,
        'userId': 'Bo',
        'displayName': 'Bo',
        'state': 'NotConnected',
        'at': reportedAt,
      });
      await tester.pumpAndSettle();

      void expectMarked() {
        expect(find.text('Not connected'), findsOneWidget);
        expect(find.text('Not joined'), findsNothing);
        expect(
          tester.widget<Text>(find.text('Not connected')).style?.color,
          error,
        );
        expect(tester.widget<Icon>(find.byIcon(Icons.link_off)).color, error);
        expect(
          tester.getTopLeft(find.text('Bo')).dy,
          lessThan(tester.getTopLeft(find.text('Ada')).dy),
        );
        // The feed says so by default, once, with the stronger marker.
        expect(find.text(notJoinedYet), findsOneWidget);
        expect(_feedDotColor(tester, notJoinedYet), error);
      }

      expectMarked();
      expect(find.text('Students (1/2 in session)'), findsOneWidget);

      // Hovering the state says what it means for the class.
      final mouse = await tester.createGesture(kind: PointerDeviceKind.mouse);
      await mouse.addPointer(location: Offset.zero);
      addTearDown(mouse.removePointer);
      await mouse.moveTo(tester.getCenter(find.text('Not connected')));
      await tester.pumpAndSettle();
      expect(find.text(tooltip), findsOneWidget);
      await mouse.moveTo(Offset.zero);
      await tester.pumpAndSettle();

      // The backend stored the report (#492). The teacher goes back to the
      // overview, leaving the session running, and opens it again: the
      // roster and the feed both still say so.
      h.sessions
        ..stored = [
          SessionRecentEvent(
            id: 'e1',
            userId: 'Bo',
            kind: 'NotConnected',
            payloadJson: '{"expectedSince":"2026-06-12T07:15:00+00:00"}',
            occurredAt: DateTime.parse(reportedAt),
          ),
        ]
        ..active = [
          ActiveSession(id: _sessionId, classId: 'c1', startedAt: _startedAt),
        ];
      await tester.tap(find.byTooltip('Leave session'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Leave running'));
      await tester.pumpAndSettle();
      expect(find.text('Allowed bundles'), findsNothing);
      await tester.tap(find.text('Resume'));
      await tester.pumpAndSettle();
      expect(find.text('Allowed bundles'), findsOneWidget);
      expect(h.hubs, hasLength(2));
      expectMarked();
      for (final raw in ['NotConnected', 'ParticipantStateChanged', '+00:00']) {
        expect(find.textContaining(raw), findsNothing, reason: raw);
      }

      // Bo's agent starts after all and joins: the marker clears at once.
      h.sessions.roster = [
        student('Ada', ParticipantLiveState.joined),
        student('Bo', ParticipantLiveState.joined),
      ];
      h.hubs.last.emit('ParticipantStateChanged', {
        'sessionId': _sessionId,
        'userId': 'Bo',
        'displayName': 'Bo',
        'state': 'Joined',
        'at': '2026-06-12T07:18:00+00:00',
      });
      await tester.pumpAndSettle();
      expect(find.text('Not connected'), findsNothing);
      expect(find.byIcon(Icons.link_off), findsNothing);
      expect(find.text('Students (2/2 in session)'), findsOneWidget);
      // The feed keeps what happened; the join itself needs no attention.
      expect(find.text(notJoinedYet), findsOneWidget);
      expect(find.text('Bo joined.'), findsNothing);
      expect(tester.takeException(), isNull);
    },
  );

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

  testWidgets(
    'the activity feed reads as sentences, shows what needs the teacher until '
    'they show all activity, and remembers that after a reload (#491)',
    (tester) async {
      final settings = createAlertSettingsStore()..write('{}');
      addTearDown(() => settings.write('{}'));
      final roster = [
        _participant('Ada', ParticipantLiveState.joined),
        _participant('Bo', ParticipantLiveState.joined),
      ];
      final h = await _bootToLiveSession(
        tester,
        roster: roster,
        settings: settings,
      );
      Map<String, dynamic> state(String who, String state) => {
        'sessionId': _sessionId,
        'userId': who,
        'displayName': who,
        'state': state,
        'at': '2026-06-12T07:20:11.3797761+00:00',
      };

      // Empty feed shows the placeholder until the first event arrives.
      expect(find.text('Waiting for events…'), findsOneWidget);
      expect(find.text('Show all activity'), findsOneWidget);

      // The session starting and a student joining don't need the teacher:
      // the feed says so, rather than looking broken.
      h.hub.emit('SessionStarted', {
        'sessionId': _sessionId,
        'classId': 'c1',
        'startedAt': '2026-06-12T07:15:00.1234567+00:00',
        'joinCode': 'ABC123',
        'apps': [
          {'matchKind': 'ProcessName', 'value': 'msedge'},
        ],
        'domains': [
          {'matchType': 'Wildcard', 'value': 'wikipedia.org'},
          {'matchType': 'Exact', 'value': 'smartschool.be'},
        ],
      });
      h.hub.emit('ParticipantStateChanged', state('Ada', 'Joined'));
      await tester.pumpAndSettle();
      expect(find.text('Waiting for events…'), findsNothing);
      expect(
        find.text('Nothing that needs your attention yet'),
        findsOneWidget,
      );
      expect(find.text('Ada joined.'), findsNothing);

      // What needs them: one plain sentence each, naming the student.
      h.hub.emit('ParticipantStateChanged', state('Bo', 'Left'));
      h.hub.emit('UnblockRequested', {
        'sessionId': _sessionId,
        'userId': 'Ada',
        'userDisplayName': 'Ada',
        'host': 'chat.example.com',
        'url': 'https://chat.example.com/',
        'requestedAt': '2026-06-12T07:21:00+00:00',
      });
      h.hub.emit('TamperDetected', {
        'sessionId': _sessionId,
        'userId': 'Ada',
        'userDisplayName': 'Ada',
        'kind': 'extension_silent',
        'detectedAt': '2026-06-12T07:22:00+00:00',
      });
      // Only the student's id: the roster names them.
      h.hub.emit('HeartbeatLost', {
        'sessionId': _sessionId,
        'userId': 'Bo',
        'lastSeenAt': '2026-06-12T07:22:30+00:00',
      });
      await tester.pumpAndSettle();

      expect(find.text('Nothing that needs your attention yet'), findsNothing);
      expect(find.text('Bo left the session.'), findsOneWidget);
      expect(
        find.text('Ada asks for access to chat.example.com.'),
        findsOneWidget,
      );
      expect(
        find.text("Ada's Anchor extension is off or not responding."),
        findsOneWidget,
      );
      expect(find.text("Bo's agent stopped reporting."), findsOneWidget);
      // Each with its local time, and no event kind, id or UTC timestamp.
      expect(
        find.textContaining(RegExp(r'^\d\d:\d\d:\d\d$')),
        findsNWidgets(4),
      );
      for (final raw in [
        'ParticipantStateChanged',
        'UnblockRequested',
        'TamperDetected',
        'HeartbeatLost',
        _sessionId,
        '+00:00',
        'state=',
      ]) {
        expect(find.textContaining(raw), findsNothing, reason: raw);
      }

      // A student who keeps reconnecting: the joins between are hidden, and
      // the run of leaving reads as one row, with its count.
      h.hub
        ..emit('ParticipantStateChanged', state('Bo', 'Joined'))
        ..emit('ParticipantStateChanged', state('Bo', 'Left'))
        ..emit('ParticipantStateChanged', state('Bo', 'Joined'))
        ..emit('ParticipantStateChanged', state('Bo', 'Left'));
      await tester.pumpAndSettle();
      expect(find.text('Bo left the session.'), findsNWidgets(2));
      expect(find.text('×2'), findsOneWidget);

      // Shown all activity, the joins and the session's start are there too,
      // the oldest at the end of the feed's list.
      await tester.tap(find.byKey(const Key('feed-show-all')));
      await tester.pumpAndSettle();
      expect(find.text('Bo joined.'), findsNWidgets(2));
      expect(find.text('×2'), findsNothing);
      final started = find.text(
        'Session started, code ABC123: 1 app and 2 websites allowed.',
      );
      await tester.scrollUntilVisible(
        started,
        120,
        scrollable: find
            .ancestor(
              of: find.text('Bo joined.').first,
              matching: find.byType(Scrollable),
            )
            .first,
      );
      expect(started, findsOneWidget);
      expect(find.text('Ada joined.'), findsOneWidget);
      expect(tester.takeException(), isNull);

      // The browser remembers the switch: after a reload, the feed still
      // shows all activity.
      final again = await _bootToLiveSession(
        tester,
        roster: roster,
        settings: settings,
        reload: true,
      );
      expect(
        tester.widget<Switch>(find.byKey(const Key('feed-show-all'))).value,
        isTrue,
      );
      again.hub.emit('ParticipantStateChanged', state('Ada', 'Joined'));
      await tester.pumpAndSettle();
      expect(find.text('Ada joined.'), findsOneWidget);
      expect(tester.takeException(), isNull);
    },
  );

  testWidgets(
    'a running session opened again shows what happened in it so far, then '
    'continues live (#492)',
    (tester) async {
      const asks = 'Ada asks for access to chat.example.com.';
      const left = 'Bo left the session.';
      const silent = "Ada's Anchor extension is off or not responding.";
      const lost = "Ada's agent stopped reporting.";
      final joinedAt = DateTime.parse('2026-06-12T07:16:00.5000000+00:00');
      const askedAt = '2026-06-12T07:20:00.1234567+00:00';
      // When the server recorded Bo's leave, and broadcast it.
      const leftAt = '2026-06-12T07:22:00.4170000+00:00';
      SessionParticipantInfo student(String name, {String? left}) =>
          SessionParticipantInfo(
            userId: name,
            displayName: name,
            joinedAt: joinedAt,
            declinedAt: null,
            leftAt: left == null ? null : DateTime.parse(left),
            state: left == null
                ? ParticipantLiveState.joined
                : ParticipantLiveState.left,
          );
      String local(String wire) {
        final t = DateTime.parse(wire).toLocal();
        String two(int n) => n.toString().padLeft(2, '0');
        return '${two(t.hour)}:${two(t.minute)}:${two(t.second)}';
      }

      double top(String text) => tester.getTopLeft(find.text(text)).dy;

      final h = await _bootToLiveSession(
        tester,
        roster: [student('Ada'), student('Bo')],
      );
      // Ada and Bo joined before the page opened, which doesn't need the
      // teacher: the feed says so rather than that it is waiting.
      expect(
        find.text('Nothing that needs your attention yet'),
        findsOneWidget,
      );

      // While the teacher watches, Ada asks for a site and Bo leaves.
      h.hub
        ..emit('UnblockRequested', {
          'sessionId': _sessionId,
          'userId': 'Ada',
          'userDisplayName': 'Ada',
          'host': 'chat.example.com',
          'url': 'https://chat.example.com/',
          'requestedAt': askedAt,
        })
        ..emit('ParticipantStateChanged', {
          'sessionId': _sessionId,
          'userId': 'Bo',
          'displayName': 'Bo',
          'state': 'Left',
          'at': leftAt,
        });
      await tester.pumpAndSettle();
      expect(find.text(asks), findsOneWidget);
      expect(find.text(left), findsOneWidget);

      // The backend stored both (Bo's leave as his agent reported it, by its
      // own clock), and Bo is out of the session.
      h.sessions
        ..stored = [
          SessionRecentEvent(
            id: 'e2',
            userId: 'Bo',
            kind: 'ManualLeave',
            payloadJson: '{}',
            occurredAt: DateTime.parse('2026-06-12T07:22:00.1000000+00:00'),
          ),
          SessionRecentEvent(
            id: 'e1',
            userId: 'Ada',
            kind: 'UnblockRequest',
            payloadJson:
                '{"url":"https://chat.example.com/","host":"chat.example.com"}',
            occurredAt: DateTime.parse(askedAt),
          ),
        ]
        ..roster = [student('Ada'), student('Bo', left: leftAt)]
        ..active = [
          ActiveSession(id: _sessionId, classId: 'c1', startedAt: _startedAt),
        ];

      // The teacher goes back to the overview, leaving the session running.
      await tester.tap(find.byTooltip('Leave session'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Leave running'));
      await tester.pumpAndSettle();
      expect(find.text('Allowed bundles'), findsNothing);

      // And opens it again. Between the page's join and its load, Ada's
      // extension goes silent: the page hears it, and it is stored too.
      const silentAt = '2026-06-12T07:25:00.2500000+00:00';
      h.sessions.whileLoadingEvents = () {
        h.hubs.last.emit('TamperDetected', {
          'sessionId': _sessionId,
          'userId': 'Ada',
          'userDisplayName': 'Ada',
          'kind': 'extension_silent',
          'detectedAt': silentAt,
        });
        h.sessions.stored = [
          SessionRecentEvent(
            id: 'e3',
            userId: 'Ada',
            kind: 'TamperDetected',
            payloadJson: '{"kind":"extension_silent"}',
            occurredAt: DateTime.parse(silentAt),
          ),
          ...h.sessions.stored,
        ];
      };
      await tester.tap(find.text('Resume'));
      await tester.pumpAndSettle();
      expect(find.text('Allowed bundles'), findsOneWidget);
      expect(h.hubs, hasLength(2));
      expect(h.hubs.last.joinCalls, 1);
      expect(h.sessions.sessionEventsCalls, 2);

      // The feed shows what happened so far, newest first, each once, at
      // the server's time of it, not "Waiting for events…".
      expect(find.text('Waiting for events…'), findsNothing);
      expect(find.text(silent), findsOneWidget);
      expect(find.text(left), findsOneWidget);
      expect(find.text(asks), findsOneWidget);
      expect(find.textContaining('×'), findsNothing);
      expect(top(silent), lessThan(top(left)));
      expect(top(left), lessThan(top(asks)));
      expect(find.text(local(leftAt)), findsOneWidget);
      expect(find.text(local(askedAt)), findsOneWidget);
      for (final raw in ['ManualLeave', 'UnblockRequest', '+00:00']) {
        expect(find.textContaining(raw), findsNothing, reason: raw);
      }

      // All activity shows the joins too, below.
      await tester.tap(find.byKey(const Key('feed-show-all')));
      await tester.pumpAndSettle();
      expect(find.text('Ada joined.'), findsOneWidget);
      expect(find.text('Bo joined.'), findsOneWidget);
      expect(top(asks), lessThan(top('Bo joined.')));

      // And it continues live, above what came before.
      h.hubs.last.emit('HeartbeatLost', {
        'sessionId': _sessionId,
        'userId': 'Ada',
        'lastSeenAt': '2026-06-12T07:26:00+00:00',
      });
      await tester.pumpAndSettle();
      expect(find.text(lost), findsOneWidget);
      expect(top(lost), lessThan(top(silent)));
      expect(tester.takeException(), isNull);
    },
  );

  testWidgets(
    "the end of the teacher's other session leaves this live view alone (#354)",
    (tester) async {
      final h = await _bootToLiveSession(tester);
      // Showing all activity (#491), the feed would show it if it took it.
      await tester.tap(find.byKey(const Key('feed-show-all')));
      await tester.pumpAndSettle();

      // The backend sends SessionEnded to the teacher's user group for every
      // session they own, so this page's connection also hears another of the
      // teacher's sessions end — e.g. a forgotten one ended automatically.
      h.hub.emit('SessionEnded', {'sessionId': _otherSessionId});
      await tester.pumpAndSettle();

      expect(find.text('Session ended — event stream stopped.'), findsNothing);
      expect(find.text('LIVE'), findsOneWidget);
      expect(find.text('Session ended.'), findsNothing);
      expect(find.text('Waiting for events…'), findsOneWidget);

      // Its own end still ends it.
      h.hub.emit('SessionEnded', {'sessionId': _sessionId});
      await tester.pumpAndSettle();

      expect(
        find.text('Session ended — event stream stopped.'),
        findsOneWidget,
      );
      expect(find.text('LIVE'), findsNothing);
      expect(find.text('Session ended.'), findsOneWidget);
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
    'after a hub reconnect the feed shows what happened while the page was '
    'offline, in its place in time, each once, then continues live (#499)',
    (tester) async {
      const asks = 'Ada asks for access to chat.example.com.';
      const left = 'Bo left the session.';
      const inPrivate = 'Ada opened an InPrivate window.';
      const switchedOff = "Bo's Anchor extension was switched off or removed.";
      const stopped = "Ada's agent stopped reporting.";
      // The server's times of them, as the hub sends them.
      const askedAt = '2026-06-12T07:20:00.1234567+00:00';
      const leftAt = '2026-06-12T07:22:00.4170000+00:00';
      const inPrivateAt = '2026-06-12T07:24:00.2500000+00:00';
      const switchedOffAt = '2026-06-12T07:26:00.5000000+00:00';
      SessionParticipantInfo student(String name, {String? left}) =>
          SessionParticipantInfo(
            userId: name,
            displayName: name,
            joinedAt: null,
            declinedAt: null,
            leftAt: left == null ? null : DateTime.parse(left),
            state: left == null
                ? ParticipantLiveState.joined
                : ParticipantLiveState.left,
          );
      SessionRecentEvent stored(
        String id,
        String userId,
        String kind,
        String at, [
        String payloadJson = '{}',
      ]) => SessionRecentEvent(
        id: id,
        userId: userId,
        kind: kind,
        payloadJson: payloadJson,
        occurredAt: DateTime.parse(at),
      );
      String local(String wire) {
        final t = DateTime.parse(wire).toLocal();
        String two(int n) => n.toString().padLeft(2, '0');
        return '${two(t.hour)}:${two(t.minute)}:${two(t.second)}';
      }

      double top(String text) => tester.getTopLeft(find.text(text)).dy;

      // A realistic window, with room for the feed under the panels.
      tester.view.physicalSize = const Size(1400, 1000);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);

      final h = await _bootToLiveSession(
        tester,
        roster: [student('Ada'), student('Bo')],
      );

      // While the teacher watches, Ada asks for a site; the backend stores it.
      h.hub.emit('UnblockRequested', {
        'sessionId': _sessionId,
        'userId': 'Ada',
        'userDisplayName': 'Ada',
        'host': 'chat.example.com',
        'url': 'https://chat.example.com/',
        'requestedAt': askedAt,
      });
      h.sessions.stored = [
        stored(
          'e1',
          'Ada',
          'UnblockRequest',
          askedAt,
          '{"url":"https://chat.example.com/","host":"chat.example.com"}',
        ),
      ];
      await tester.pumpAndSettle();
      expect(find.text(asks), findsOneWidget);

      // The backend restarts. While the page is offline Bo leaves (his agent
      // reports it by its own clock, the server keeps when it heard) and Ada
      // opens InPrivate; neither broadcast reaches the page.
      h.hub.drop();
      h.hub
        ..emit('ParticipantStateChanged', {
          'sessionId': _sessionId,
          'userId': 'Bo',
          'displayName': 'Bo',
          'state': 'Left',
          'at': leftAt,
        })
        ..emit('TamperDetected', {
          'sessionId': _sessionId,
          'userId': 'Ada',
          'userDisplayName': 'Ada',
          'kind': 'inprivate_opened',
          'detectedAt': inPrivateAt,
        });
      h.sessions
        ..roster = [student('Ada'), student('Bo', left: leftAt)]
        ..stored = [
          stored(
            'e3',
            'Ada',
            'TamperDetected',
            inPrivateAt,
            '{"kind":"inprivate_opened"}',
          ),
          stored('e2', 'Bo', 'ManualLeave', '2026-06-12T07:22:00.1+00:00'),
          ...h.sessions.stored,
        ];
      await tester.pumpAndSettle();
      expect(find.text(left), findsNothing);
      expect(find.text(inPrivate), findsNothing);

      // The connection comes back. Between the page's join and its load,
      // Bo's extension is switched off: the page hears it, and it is stored.
      final loads = h.sessions.sessionEventsCalls;
      h.sessions.whileLoadingEvents = () {
        h.hub.emit('TamperDetected', {
          'sessionId': _sessionId,
          'userId': 'Bo',
          'userDisplayName': 'Bo',
          'kind': 'extension_disabled',
          'detectedAt': switchedOffAt,
        });
        h.sessions.stored = [
          stored(
            'e4',
            'Bo',
            'TamperDetected',
            switchedOffAt,
            '{"kind":"extension_disabled"}',
          ),
          ...h.sessions.stored,
        ];
      };
      h.hub.reconnect();
      await tester.pumpAndSettle();
      expect(h.hub.joinCalls, 2);
      expect(h.sessions.sessionEventsCalls, loads + 1);

      // The feed shows what happened while it was offline, between what it
      // heard before and after, each once, at the server's time of it.
      for (final sentence in [switchedOff, inPrivate, left, asks]) {
        expect(find.text(sentence), findsOneWidget, reason: sentence);
      }
      expect(find.textContaining('×'), findsNothing);
      expect(top(switchedOff), lessThan(top(inPrivate)));
      expect(top(inPrivate), lessThan(top(left)));
      expect(top(left), lessThan(top(asks)));
      expect(find.text(local(inPrivateAt)), findsOneWidget);
      expect(find.text(local(leftAt)), findsOneWidget);
      for (final raw in ['ManualLeave', 'TamperDetected', '+00:00']) {
        expect(find.textContaining(raw), findsNothing, reason: raw);
      }

      // And it continues live, on top.
      h.hub.emit('HeartbeatLost', {
        'sessionId': _sessionId,
        'userId': 'Ada',
        'lastSeenAt': '2026-06-12T07:27:00+00:00',
      });
      await tester.pumpAndSettle();
      expect(find.text(stopped), findsOneWidget);
      expect(top(stopped), lessThan(top(switchedOff)));
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

  testWidgets(
    'the live view renders as the paper instrument panel (AD4, #169)',
    (tester) async {
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

      // A pushed event lands as a hairline feed row, a sentence about the
      // student — the instrument read-out (#491).
      h.hub.emit('HeartbeatLost', {'userId': 'Ada'});
      await tester.pumpAndSettle();
      expect(find.text("Ada's agent stopped reporting."), findsOneWidget);

      // The composition holds under the real shell + real Fraunces / Space Mono
      // at this window size — no RenderFlex overflow or other exception.
      expect(tester.takeException(), isNull);
    },
  );

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
    // That height in a window too narrow for the two columns (#494), where
    // the panels stack over the feed as #422 found them.
    const shortNarrow = Size(860, 600);

    final sixStudents = [
      for (final name in ['Ada', 'Bo', 'Cas', 'Dina', 'Eli', 'Fien'])
        _participant(name, ParticipantLiveState.joined),
    ];

    /// The part of the window the page scrolls in, under the shell's chrome:
    /// the outermost scroll view, as on a wide window the right column has
    /// one of its own (#494).
    Rect page(WidgetTester tester) =>
        tester.getRect(find.byType(CustomScrollView).first);

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
        // Stacked: #494 gives the laptop's 1280 px two columns, each
        // scrolling on its own, which its own group below covers.
        window(tester, shortNarrow);
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
        final event = find.text(
          "Ada's agent stopped reporting.",
          skipOffstage: false,
        );
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
        // its least height, so it takes all of it, as before #422 (in the
        // right column since #494).
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
                    of: find.byType(CustomScrollView).first,
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

  group('the live view in two columns on a wide window (#494)', () {
    void window(WidgetTester tester, Size size) {
      tester.view.physicalSize = size;
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);
    }

    /// A class of [count], named so they sort in this order: Student 01, …
    List<SessionParticipantInfo> students(int count) => [
      for (var i = 1; i <= count; i++)
        _participant(
          'Student ${i.toString().padLeft(2, '0')}',
          ParticipantLiveState.joined,
        ),
    ];

    UnblockRequestSummary request(String host) {
      final now = DateTime(2026, 6, 12, 9, 20);
      return UnblockRequestSummary(
        host: host,
        count: 1,
        firstRequestedAt: now,
        latestRequestedAt: now,
        requesters: [
          UnblockRequestRequester(
            userId: 'Student 01',
            displayName: 'Student 01',
            requestedAt: now,
          ),
        ],
      );
    }

    /// The part of the window the page lays out in, under the shell's chrome.
    Rect page(WidgetTester tester) =>
        tester.getRect(find.byType(CustomScrollView).first);

    bool inView(WidgetTester tester, Finder finder) {
      // A row a lazy list hasn't built is nowhere near the window.
      if (finder.evaluate().isEmpty) return false;
      final rect = tester.getRect(finder);
      return rect.top >= page(tester).top && rect.bottom <= page(tester).bottom;
    }

    /// Turns the mouse wheel by [dy] at [at], the way a teacher scrolls.
    Future<void> wheelAt(WidgetTester tester, Offset at, double dy) async {
      final pointer = TestPointer(1, PointerDeviceKind.mouse)..hover(at);
      await tester.sendEventToBinding(pointer.scroll(Offset(0, dy)));
      await tester.pumpAndSettle();
    }

    /// The roster's list: the one around [student]'s row.
    Rect rosterList(WidgetTester tester, String student) => tester.getRect(
      find
          .ancestor(of: find.text(student), matching: find.byType(ListView))
          .first,
    );

    testWidgets(
      'on a classroom screen the whole class shows at once on the left, and '
      'the requests, the bundles and the feed share the right',
      (tester) async {
        // A 1080p classroom screen, less the browser's chrome and the
        // taskbar: some 940 px high. (Narrower than its 1920 px, to stay
        // within the e2e browser's own window.)
        window(tester, const Size(1600, 940));
        final h = await _bootToLiveSession(tester, roster: students(18));
        h.sessions.pending = [request('chat.example.com')];
        h.hub.emit('UnblockRequested', {'host': 'chat.example.com'});
        await tester.pumpAndSettle();

        // Every student in view, with the real fonts: no box of 240 px to
        // scroll in, and no page to scroll either.
        expect(find.text('Students (18/18 in session)'), findsOneWidget);
        for (final p in students(18)) {
          expect(
            inView(tester, find.text(p.displayName)),
            isTrue,
            reason: p.displayName,
          );
        }
        final roster = rosterList(tester, 'Student 01');
        expect(roster.height, greaterThan(240));
        expect(roster.bottom, page(tester).bottom);

        // Right of the students, top to bottom: the requests the teacher
        // acts on, the bundles, then the feed down to the window's bottom.
        final requests = tester.getRect(find.text('Pending requests'));
        final bundles = tester.getRect(find.text('Allowed bundles'));
        final activity = tester.getRect(find.text('Activity'));
        expect(roster.right, lessThan(requests.left));
        expect(bundles.left, requests.left);
        expect(activity.left, requests.left);
        expect(
          requests.top,
          tester.getRect(find.text('Students (18/18 in session)')).top,
        );
        expect(requests.bottom, lessThan(bundles.top));
        expect(bundles.bottom, lessThan(activity.top));
        // The feed's box, around its header.
        final feed = tester.getRect(
          find
              .ancestor(
                of: find.text('Activity'),
                matching: find.byType(SizedBox),
              )
              .first,
        );
        expect(feed.bottom, page(tester).bottom);
        expect(feed.left, greaterThanOrEqualTo(roster.right));
        // The students' column keeps its width; the right one takes the rest.
        expect(roster.right, lessThanOrEqualTo(448));
        expect(feed.width, greaterThan(page(tester).width / 2));
        final pageScroll = tester
            .state<ScrollableState>(
              find
                  .descendant(
                    of: find.byType(CustomScrollView).first,
                    matching: find.byType(Scrollable),
                  )
                  .first,
            )
            .position;
        expect(pageScroll.maxScrollExtent, 0);

        // The right column works as the panels did: approve, toggle a
        // bundle, read the feed.
        await tester.tap(find.widgetWithText(OutlinedButton, 'Approve'));
        await tester.pumpAndSettle();
        expect(h.sessions.perStudentApprovals, [
          ('Student 01', 'chat.example.com'),
        ]);
        await tester.tap(find.widgetWithText(FilterChip, 'Math'));
        await tester.pumpAndSettle();
        expect(h.sessions.updateBundlesCalls, [
          ['b1'],
        ]);

        // A student who needs the teacher tops the column, still in view,
        // and the feed says so on the right (#493, #491).
        h.sessions.roster = [
          ...students(18).skip(1),
          _participant('Student 01', ParticipantLiveState.heartbeatStale),
        ];
        h.hub.emit('HeartbeatLost', {
          'sessionId': _sessionId,
          'userId': 'Student 01',
        });
        await tester.pumpAndSettle();
        expect(
          tester.getRect(find.text('Agent stopped reporting')).top,
          lessThan(tester.getRect(find.text('Student 02')).top),
        );
        final sentence = find.text("Student 01's agent stopped reporting.");
        expect(inView(tester, sentence), isTrue);
        expect(tester.getRect(sentence).left, greaterThan(roster.right));
        expect(tester.takeException(), isNull);

        // Once the session ends the view is one column again: the students
        // and the bundles go, the feed spans the page under the notice.
        h.hub.emit('SessionEnded', {'sessionId': _sessionId});
        await tester.pumpAndSettle();
        expect(find.text('Students (17/18 in session)'), findsNothing);
        expect(find.text('Allowed bundles'), findsNothing);
        expect(
          tester.getRect(find.text('Activity')).left,
          tester
              .getRect(find.text('Session ended — event stream stopped.'))
              .left,
        );
        expect(tester.takeException(), isNull);
      },
    );

    testWidgets(
      'on a short laptop window each column scrolls on its own, under the '
      'header',
      (tester) async {
        // A laptop at 150% (#422): about 1280x600 is left for the page.
        window(tester, const Size(1280, 600));
        final h = await _bootToLiveSession(tester, roster: students(30));
        h.sessions.pending = [request('chat.example.com')];
        h.hub.emit('UnblockRequested', {'host': 'chat.example.com'});
        await tester.pumpAndSettle();
        expect(tester.takeException(), isNull);

        final first = find.text('Student 01');
        final last = find.text('Student 30', skipOffstage: false);
        final requests = find.text('Pending requests');
        // Where [finder] sits under the header. The page itself scrolls only
        // on a window shorter than the header and the columns' least height,
        // which this one about is: what a column does shows against it.
        double underHeader(Finder finder) =>
            tester.getRect(finder).top -
            tester.getRect(find.text('Math 101')).top;
        final requestsAt = underHeader(requests);
        expect(inView(tester, first), isTrue);
        expect(inView(tester, last), isFalse, reason: 'below the window');
        // Side by side, the requests level with the students.
        final studentsLabel = find.text('Students (30/30 in session)');
        expect(
          tester.getRect(first).right,
          lessThan(tester.getRect(requests).left),
        );
        expect(underHeader(requests), underHeader(studentsLabel));

        // The wheel over the students scrolls their list to the last one;
        // the right column stays where it is.
        final overRoster = tester.getCenter(first);
        for (var i = 0; i < 40 && !inView(tester, last); i++) {
          await wheelAt(tester, overRoster, 80);
        }
        expect(inView(tester, last), isTrue);
        expect(underHeader(requests), requestsAt);

        // The wheel over the right column scrolls it down to the feed, under
        // the requests and the bundles; the students stay where they are.
        final lastAt = underHeader(last);
        h.hub.emit('HeartbeatLost', {
          'sessionId': _sessionId,
          'userId': 'Student 02',
        });
        await tester.pumpAndSettle();
        final event = find.text(
          "Student 02's agent stopped reporting.",
          skipOffstage: false,
        );
        await wheelAt(tester, tester.getCenter(requests), 5000);
        expect(inView(tester, event), isTrue);
        expect(inView(tester, find.text('Activity')), isTrue);
        expect(underHeader(last), lastAt);
        // The students are still in view beside it, their label too.
        expect(inView(tester, last), isTrue);
        expect(inView(tester, studentsLabel), isTrue);
        expect(tester.takeException(), isNull);
      },
    );

    testWidgets(
      'a narrower window stacks the panels as before, and the layout follows '
      'the window across the breakpoint',
      (tester) async {
        window(tester, const Size(1400, 1000));
        final h = await _bootToLiveSession(tester, roster: students(18));
        h.sessions.pending = [request('chat.example.com')];
        h.hub.emit('UnblockRequested', {'host': 'chat.example.com'});
        await tester.pumpAndSettle();
        Rect rect(String text) => tester.getRect(find.text(text));
        const studentsLabel = 'Students (18/18 in session)';
        expect(rect('Pending requests').top, rect(studentsLabel).top);

        // The teacher makes the window narrower: one column, the requests,
        // the students (capped, as before), the bundles, then the feed.
        tester.view.physicalSize = const Size(860, 1000);
        await tester.pumpAndSettle();
        expect(tester.takeException(), isNull);
        final requests = rect('Pending requests');
        for (final label in [studentsLabel, 'Allowed bundles', 'Activity']) {
          expect(rect(label).left, requests.left, reason: label);
        }
        expect(requests.bottom, lessThan(rect(studentsLabel).top));
        expect(
          rect(studentsLabel).bottom,
          lessThan(rect('Allowed bundles').top),
        );
        expect(rosterList(tester, 'Student 01').height, lessThanOrEqualTo(240));
        expect(find.text('chat.example.com'), findsOneWidget);

        // And wide again: two columns, the requests still there to approve.
        tester.view.physicalSize = const Size(1400, 1000);
        await tester.pumpAndSettle();
        expect(tester.takeException(), isNull);
        expect(rect('Pending requests').top, rect(studentsLabel).top);
        await tester.tap(find.widgetWithText(OutlinedButton, 'Approve'));
        await tester.pumpAndSettle();
        expect(h.sessions.perStudentApprovals, [
          ('Student 01', 'chat.example.com'),
        ]);
        expect(tester.takeException(), isNull);
      },
    );
  });
}
