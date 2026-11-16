import 'dart:async';

import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/auth_token_store.dart';
import 'package:anchor_dashboard/api/bundles_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/l10n/app_localizations.dart';
import 'package:anchor_dashboard/pages/session_page.dart';
import 'package:anchor_dashboard/realtime/session_hub_client.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

// #365: SignalR keeps no group membership across a reconnect, and every live
// roster signal goes to the session group. After its hub connection reconnects
// the live page has to join its session again and re-fetch what it missed
// while it was offline, or its roster and pending requests freeze.
//
// #370: however long the connection is down, the page says that live updates
// are paused, catches up once it's back, and starts a closed connection again
// when the teacher asks or comes back to the page.
//
// #499: its activity feed catches up too, with what was broadcast while it
// was offline, in its place in time.

const _sessionId = 'aaaaaaaa-0000-0000-0000-000000000001';

ApiClient _dummyClient() => ApiClient(
  baseUrl: Uri.parse('http://localhost'),
  tokenProvider: () async => null,
);

SessionParticipantInfo _participant(
  String name,
  ParticipantLiveState state, {
  DateTime? leftAt,
}) => SessionParticipantInfo(
  userId: name,
  displayName: name,
  joinedAt: null,
  declinedAt: null,
  leftAt: leftAt,
  state: state,
  tampered: false,
);

/// Stands in for the SignalR client and routes the way the hub does: nothing
/// reaches the page while its connection is down, and a roster signal goes to
/// the session group, so it reaches the page only while its current connection
/// has joined that group. A reconnect is a new connection, in no session group
/// until the page calls JoinSession again.
class _StubHub extends SessionHubClient {
  _StubHub()
    : super(apiBaseUrl: Uri.parse('http://localhost'), tokenProvider: _noToken);

  static Future<String?> _noToken() async => null;

  final _ctrl = StreamController<SessionEvent>.broadcast();
  final _reconnectedCtrl = StreamController<void>.broadcast();
  final _linkCtrl = StreamController<SessionHubLinkState>.broadcast();
  bool _online = true;
  bool _inSessionGroup = false;
  int joinCalls = 0;
  int restartCalls = 0;

  /// What JoinSession throws, e.g. the hub refusing a session that has ended.
  Object? joinError;

  /// What the first connect throws, e.g. the backend being unreachable.
  Object? connectError;

  @override
  Stream<SessionEvent> get events => _ctrl.stream;

  @override
  Stream<void> get reconnected => _reconnectedCtrl.stream;

  @override
  Stream<SessionHubLinkState> get linkState => _linkCtrl.stream;

  /// The backend broadcasts a roster signal to the session group.
  void broadcastToSessionGroup(String kind, Map<String, dynamic> payload) {
    if (!_online || !_inSessionGroup) return;
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

  /// The connection closes and nothing retries it, e.g. the server turned it
  /// away.
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
  Future<void> connect() async {
    final error = connectError;
    if (error == null) return;
    _online = false;
    _linkCtrl.add(SessionHubLinkState.disconnected);
    throw error;
  }

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

const _reconnecting =
    'Connection lost — reconnecting. Live updates are paused.';
const _disconnected = 'Disconnected — live updates are paused.';

/// Server state the page reads; a test changes it, then lets the page find out.
class _FakeSessions extends SessionsApi {
  _FakeSessions() : super(_dummyClient());

  List<SessionParticipantInfo> roster = [
    _participant('Ada', ParticipantLiveState.joined),
  ];
  List<UnblockRequestSummary> pending = const [];
  DateTime? endedAt;
  int getSessionCalls = 0;
  int unblockRequestsCalls = 0;

  /// What the backend stored for the session, newest first, which the page
  /// loads its feed's history from (#492, #499).
  List<SessionRecentEvent> stored = const [];

  /// How the backend answers the [call]th load of them (from 1), when set:
  /// e.g. late. Otherwise at once, with [stored].
  Future<List<SessionRecentEvent>> Function(int call)? answerEvents;

  /// What happens while the page loads them: e.g. an event the hub
  /// broadcasts between the page's join and the load.
  void Function()? whileLoadingEvents;

  /// The page's detail and history loads, in order.
  final List<String> log = [];
  int sessionEventsCalls = 0;

  @override
  Future<SessionDetail> getSession(String sessionId) async {
    getSessionCalls++;
    log.add('detail');
    return SessionDetail(
      id: sessionId,
      classId: 'c1',
      className: 'Class',
      joinCode: 'ABC123',
      startedAt: DateTime(2026, 5, 26, 9, 15),
      endedAt: endedAt,
      summaries: const [],
      recentEvents: const [],
      participants: roster,
      bundles: const [],
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
    final call = ++sessionEventsCalls;
    log.add('events');
    whileLoadingEvents?.call();
    final answer = answerEvents;
    return answer == null ? stored : await answer(call);
  }
}

class _FakeBundles extends BundlesApi {
  _FakeBundles() : super(_dummyClient());

  @override
  Future<List<BundleSummary>> list({bool includeArchived = false}) async =>
      const [];
}

UnblockRequestSummary _request(String host) {
  final at = DateTime(2026, 5, 26, 9, 20);
  return UnblockRequestSummary(
    host: host,
    count: 1,
    firstRequestedAt: at,
    latestRequestedAt: at,
    requesters: [
      UnblockRequestRequester(
        userId: 'Ada',
        displayName: 'Ada',
        requestedAt: at,
      ),
    ],
  );
}

Future<({_StubHub hub, _FakeSessions sessions})> _pumpLiveSession(
  WidgetTester tester, {
  Object? connectError,
  Object? joinError,
  _FakeSessions? sessions,
}) async {
  // Room for the pending-requests panel next to the roster.
  tester.view.physicalSize = const Size(1400, 1000);
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.resetPhysicalSize);
  addTearDown(tester.view.resetDevicePixelRatio);

  final hub = _StubHub()
    ..connectError = connectError
    ..joinError = joinError;
  sessions ??= _FakeSessions();
  await tester.pumpWidget(
    MaterialApp(
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      home: SessionPage(
        sessionId: _sessionId,
        tokens: AuthTokenStore(),
        sessions: sessions,
        bundles: _FakeBundles(),
        apiBaseUrl: Uri.parse('http://localhost'),
        hubClientFactory: ({required apiBaseUrl, required tokenProvider}) =>
            hub,
      ),
    ),
  );
  await tester.pumpAndSettle();
  return (hub: hub, sessions: sessions);
}

void main() {
  testWidgets(
    'after a hub reconnect the page joins its session again and gets roster signals (#365)',
    (tester) async {
      final h = await _pumpLiveSession(tester);
      expect(h.hub.joinCalls, 1);
      expect(find.text('In session'), findsOneWidget);

      h.hub.drop();
      h.hub.reconnect();
      await tester.pumpAndSettle();

      expect(h.hub.joinCalls, 2);

      // A roster signal after the reconnect reaches the page.
      h.sessions.roster = [
        _participant('Ada', ParticipantLiveState.heartbeatStale),
      ];
      h.hub.broadcastToSessionGroup('ParticipantStateChanged', {
        'sessionId': _sessionId,
        'userId': 'Ada',
        'displayName': 'Ada',
        'state': 'HeartbeatStale',
      });
      await tester.pumpAndSettle();

      expect(find.text('Agent stopped reporting'), findsOneWidget);
      expect(find.text('In session'), findsNothing);
      expect(find.text("Ada's agent stopped reporting."), findsOneWidget);
    },
  );

  testWidgets(
    'after a hub reconnect the page re-fetches the roster and pending requests it missed (#365)',
    (tester) async {
      final h = await _pumpLiveSession(tester);
      final detailFetches = h.sessions.getSessionCalls;
      final pendingFetches = h.sessions.unblockRequestsCalls;
      expect(find.text('Pending requests'), findsNothing);

      // While the page is offline Ada leaves and asks for a site; neither
      // broadcast reaches it.
      h.hub.drop();
      h.sessions.roster = [_participant('Ada', ParticipantLiveState.left)];
      h.hub.broadcastToSessionGroup('ParticipantStateChanged', {
        'sessionId': _sessionId,
        'userId': 'Ada',
      });
      h.sessions.pending = [_request('chat.example.com')];
      h.hub.broadcastToSessionGroup('UnblockRequested', {
        'sessionId': _sessionId,
        'host': 'chat.example.com',
      });
      await tester.pumpAndSettle();
      expect(find.text('In session'), findsOneWidget);
      expect(find.text('Pending requests'), findsNothing);

      h.hub.reconnect();
      await tester.pumpAndSettle();

      expect(h.sessions.getSessionCalls, detailFetches + 1);
      expect(h.sessions.unblockRequestsCalls, pendingFetches + 1);
      expect(find.text('Left'), findsOneWidget);
      expect(find.text('In session'), findsNothing);
      expect(find.text('Pending requests'), findsOneWidget);
      expect(find.text('chat.example.com'), findsOneWidget);
    },
  );

  testWidgets(
    'a session that ended while the page was offline shows as ended after the reconnect (#365)',
    (tester) async {
      final h = await _pumpLiveSession(tester);
      expect(find.text('LIVE'), findsOneWidget);

      // The session ends while the connection is down, so its SessionEnded
      // never reaches the page, and the hub refuses to join an ended session.
      h.hub.drop();
      h.sessions.endedAt = DateTime(2026, 5, 26, 10, 0);
      h.hub.joinError = Exception('Session not found or already ended.');
      h.hub.reconnect();
      await tester.pumpAndSettle();

      expect(
        find.text('Session ended — event stream stopped.'),
        findsOneWidget,
      );
      expect(find.text('LIVE'), findsNothing);
    },
  );

  testWidgets(
    'while the connection is down the page says live updates are paused, and a reconnect minutes later still catches it up (#370)',
    (tester) async {
      final h = await _pumpLiveSession(tester);
      expect(find.text(_reconnecting), findsNothing);
      final detailFetches = h.sessions.getSessionCalls;
      final pendingFetches = h.sessions.unblockRequestsCalls;

      h.hub.drop();
      await tester.pumpAndSettle();
      expect(find.text(_reconnecting), findsOneWidget);
      // The session itself is still running.
      expect(find.text('LIVE'), findsOneWidget);

      // Ada leaves and asks for a site while the backend is down for longer
      // than SignalR's default policy would have kept trying (~42 s).
      h.sessions.roster = [_participant('Ada', ParticipantLiveState.left)];
      h.sessions.pending = [_request('chat.example.com')];
      await tester.pump(const Duration(minutes: 5));
      expect(find.text(_reconnecting), findsOneWidget);
      expect(find.text('In session'), findsOneWidget);

      h.hub.reconnect();
      await tester.pumpAndSettle();

      expect(find.text(_reconnecting), findsNothing);
      expect(h.hub.joinCalls, 2);
      expect(h.sessions.getSessionCalls, detailFetches + 1);
      expect(h.sessions.unblockRequestsCalls, pendingFetches + 1);
      expect(find.text('Left'), findsOneWidget);
      expect(find.text('chat.example.com'), findsOneWidget);
    },
  );

  testWidgets(
    'a closed connection offers Reconnect, which joins the session again and catches up (#370)',
    (tester) async {
      final h = await _pumpLiveSession(tester);
      final detailFetches = h.sessions.getSessionCalls;

      h.hub.close();
      h.sessions.roster = [_participant('Ada', ParticipantLiveState.left)];
      await tester.pumpAndSettle();
      expect(find.text(_disconnected), findsOneWidget);
      expect(h.hub.restartCalls, 0);

      await tester.tap(find.widgetWithText(TextButton, 'Reconnect'));
      await tester.pumpAndSettle();

      expect(h.hub.restartCalls, 1);
      expect(find.text(_disconnected), findsNothing);
      expect(find.text('Reconnect'), findsNothing);
      expect(h.hub.joinCalls, 2);
      expect(h.sessions.getSessionCalls, detailFetches + 1);
      expect(find.text('Left'), findsOneWidget);
    },
  );

  testWidgets(
    'coming back to the page starts a closed connection again (#370)',
    (tester) async {
      final h = await _pumpLiveSession(tester);

      // While connected, coming back to the page leaves it alone.
      tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.inactive);
      tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.resumed);
      await tester.pumpAndSettle();
      expect(h.hub.restartCalls, 0);

      h.hub.close();
      await tester.pumpAndSettle();
      expect(find.text(_disconnected), findsOneWidget);
      tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.inactive);
      tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.resumed);
      await tester.pumpAndSettle();

      expect(h.hub.restartCalls, 1);
      expect(h.hub.joinCalls, 2);
      expect(find.text(_disconnected), findsNothing);
    },
  );

  testWidgets(
    'when the first connect fails the page offers Reconnect, which connects it (#370)',
    (tester) async {
      final h = await _pumpLiveSession(
        tester,
        connectError: Exception('backend unreachable'),
      );
      expect(h.hub.joinCalls, 0);
      expect(find.text('Connecting to the live feed…'), findsNothing);
      expect(find.text(_disconnected), findsOneWidget);

      await tester.tap(find.widgetWithText(TextButton, 'Reconnect'));
      await tester.pumpAndSettle();

      expect(h.hub.joinCalls, 1);
      expect(find.text(_disconnected), findsNothing);

      // Roster signals reach it now.
      h.sessions.roster = [
        _participant('Ada', ParticipantLiveState.heartbeatStale),
      ];
      h.hub.broadcastToSessionGroup('ParticipantStateChanged', {
        'sessionId': _sessionId,
        'userId': 'Ada',
      });
      await tester.pumpAndSettle();
      expect(find.text('Agent stopped reporting'), findsOneWidget);
    },
  );

  testWidgets(
    'a join the hub refused at the start is cleared once a reconnect joins (#370)',
    (tester) async {
      final h = await _pumpLiveSession(
        tester,
        joinError: Exception('hub hiccup'),
      );
      expect(find.textContaining('Could not connect'), findsOneWidget);

      h.hub.joinError = null;
      h.hub.drop();
      h.hub.reconnect();
      await tester.pumpAndSettle();

      expect(h.hub.joinCalls, 2);
      expect(find.textContaining('Could not connect'), findsNothing);
    },
  );

  group('#499: the feed after a reconnect', () {
    const asks = 'Ada asks for access to chat.example.com.';
    const left = 'Bo left the session.';
    const inPrivate = 'Ada opened an InPrivate window.';
    const switchedOff = "Bo's Anchor extension was switched off or removed.";
    const stopped = "Ada's agent stopped reporting.";
    // The server's times of them, as the hub sends them.
    const askedAt = '2026-05-26T07:20:00.1234567+00:00';
    const leftAt = '2026-05-26T07:22:00.4000000+00:00';
    const inPrivateAt = '2026-05-26T07:24:00+00:00';
    const switchedOffAt = '2026-05-26T07:26:00.5000000+00:00';

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

    double top(WidgetTester tester, String text) =>
        tester.getTopLeft(find.text(text)).dy;

    testWidgets('it shows what the page missed while offline, in its place in '
        'time, each once, then continues live', (tester) async {
      final h = await _pumpLiveSession(tester);
      h.sessions.roster = [
        _participant('Ada', ParticipantLiveState.joined),
        _participant('Bo', ParticipantLiveState.joined),
      ];

      // The page hears Ada ask for a site, and the backend stores it.
      h.hub.broadcastToSessionGroup('UnblockRequested', {
        'sessionId': _sessionId,
        'userId': 'Ada',
        'userDisplayName': 'Ada',
        'host': 'chat.example.com',
        'requestedAt': askedAt,
      });
      h.sessions.stored = [
        stored(
          'e1',
          'Ada',
          'UnblockRequest',
          askedAt,
          '{"host":"chat.example.com"}',
        ),
      ];
      await tester.pumpAndSettle();
      expect(find.text(asks), findsOneWidget);

      // Offline, it misses Bo leaving (his agent reports it by its own
      // clock; the server keeps when it heard) and Ada opening InPrivate.
      h.hub.drop();
      h.hub.broadcastToSessionGroup('ParticipantStateChanged', {
        'sessionId': _sessionId,
        'userId': 'Bo',
        'displayName': 'Bo',
        'state': 'Left',
        'at': leftAt,
      });
      h.hub.broadcastToSessionGroup('TamperDetected', {
        'sessionId': _sessionId,
        'userId': 'Ada',
        'userDisplayName': 'Ada',
        'kind': 'inprivate_opened',
        'detectedAt': inPrivateAt,
      });
      h.sessions
        ..roster = [
          _participant('Ada', ParticipantLiveState.joined),
          _participant(
            'Bo',
            ParticipantLiveState.left,
            leftAt: DateTime.parse(leftAt),
          ),
        ]
        ..stored = [
          stored(
            'e3',
            'Ada',
            'TamperDetected',
            inPrivateAt,
            '{"kind":"inprivate_opened"}',
          ),
          stored('e2', 'Bo', 'ManualLeave', '2026-05-26T07:22:00.1+00:00'),
          ...h.sessions.stored,
        ];
      await tester.pumpAndSettle();
      expect(find.text(left), findsNothing);
      expect(find.text(inPrivate), findsNothing);

      // Back, between the page's join and its load Bo's extension goes off:
      // the page hears it, and it is stored too.
      final loadsBefore = h.sessions.log.length;
      h.sessions.whileLoadingEvents = () {
        h.hub.broadcastToSessionGroup('TamperDetected', {
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

      // It loaded the history again, after the roster it rebuilds the leave
      // from (and the tamper it heard re-reads the roster again).
      expect(h.sessions.log.sublist(loadsBefore).take(2), ['detail', 'events']);
      for (final sentence in [switchedOff, inPrivate, left, asks]) {
        expect(find.text(sentence), findsOneWidget, reason: sentence);
      }
      expect(find.textContaining('×'), findsNothing);
      expect(top(tester, switchedOff), lessThan(top(tester, inPrivate)));
      expect(top(tester, inPrivate), lessThan(top(tester, left)));
      expect(top(tester, left), lessThan(top(tester, asks)));

      // And it continues live, on top.
      h.hub.broadcastToSessionGroup('HeartbeatLost', {
        'sessionId': _sessionId,
        'userId': 'Ada',
        'lastSeenAt': '2026-05-26T07:27:00+00:00',
      });
      await tester.pumpAndSettle();
      expect(top(tester, stopped), lessThan(top(tester, switchedOff)));
      expect(tester.takeException(), isNull);
    });

    testWidgets("a load that answers late doesn't undo a newer one", (
      tester,
    ) async {
      final firstLoad = Completer<List<SessionRecentEvent>>();
      final sessions = _FakeSessions();
      final h = await _pumpLiveSession(
        tester,
        sessions: sessions
          ..answerEvents = (call) async =>
              call == 1 ? firstLoad.future : sessions.stored,
      );
      expect(h.sessions.sessionEventsCalls, 1);

      // The page reconnects before its first load answers, and Ada opened
      // InPrivate meanwhile.
      h.hub.drop();
      h.sessions.stored = [
        stored(
          'e1',
          'Ada',
          'TamperDetected',
          inPrivateAt,
          '{"kind":"inprivate_opened"}',
        ),
      ];
      h.hub.reconnect();
      await tester.pumpAndSettle();
      expect(h.sessions.sessionEventsCalls, 2);
      expect(find.text(inPrivate), findsOneWidget);

      // The first load answers now, with what the backend had then.
      firstLoad.complete(const []);
      await tester.pumpAndSettle();
      expect(find.text(inPrivate), findsOneWidget);
    });
  });
}
