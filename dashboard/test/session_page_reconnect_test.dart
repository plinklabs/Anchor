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
  bool _online = true;
  bool _inSessionGroup = false;
  int joinCalls = 0;

  /// What JoinSession throws, e.g. the hub refusing a session that has ended.
  Object? joinError;

  @override
  Stream<SessionEvent> get events => _ctrl.stream;

  @override
  Stream<void> get reconnected => _reconnectedCtrl.stream;

  /// The backend broadcasts a roster signal to the session group.
  void broadcastToSessionGroup(String kind, Map<String, dynamic> payload) {
    if (!_online || !_inSessionGroup) return;
    _ctrl.add(SessionEvent(kind: kind, payload: payload, at: DateTime.now()));
  }

  /// The connection drops, e.g. a network blip or a backend restart.
  void drop() {
    _online = false;
    _inSessionGroup = false;
  }

  /// SignalR's automatic reconnect brings the connection back.
  void reconnect() {
    _online = true;
    _reconnectedCtrl.add(null);
  }

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
  }
}

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
  WidgetTester tester,
) async {
  // Room for the pending-requests panel next to the roster.
  tester.view.physicalSize = const Size(1400, 1000);
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.resetPhysicalSize);
  addTearDown(tester.view.resetDevicePixelRatio);

  final hub = _StubHub();
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
}
