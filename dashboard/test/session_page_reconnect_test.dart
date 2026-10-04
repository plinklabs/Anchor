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

const _sessionId = 'aaaaaaaa-0000-0000-0000-000000000001';

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

  @override
  Future<SessionDetail> getSession(String sessionId) async {
    getSessionCalls++;
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
}) async {
  // Room for the pending-requests panel next to the roster.
  tester.view.physicalSize = const Size(1400, 1000);
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.resetPhysicalSize);
  addTearDown(tester.view.resetDevicePixelRatio);

  final hub = _StubHub()
    ..connectError = connectError
    ..joinError = joinError;
  final sessions = _FakeSessions();
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
      });
      await tester.pumpAndSettle();

      expect(find.text('Agent stopped reporting'), findsOneWidget);
      expect(find.text('In session'), findsNothing);
      expect(find.text('ParticipantStateChanged'), findsOneWidget);
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
}
