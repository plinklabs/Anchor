import 'dart:async';

import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/auth_token_store.dart';
import 'package:anchor_dashboard/api/bundles_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/l10n/app_localizations.dart';
import 'package:anchor_dashboard/pages/past_session_page.dart';
import 'package:anchor_dashboard/pages/session_page.dart';
import 'package:anchor_dashboard/realtime/session_hub_client.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:plink_design_system/plink_design_system.dart';

// #382: since #369 the session detail and its unblock requests answer only the
// teacher who owns the session; everyone else gets a 403. A teacher who opens
// a colleague's session, from a link or a stale bookmark, must see the calm
// not-available notice the other pages show for a 403 (#278), never the raw
// `ApiException(403):` text, and neither 403 may go uncaught.
//
// The live page must not open the hub for a session it may not read: the hub
// refuses the join (#366), and since #370 the connection never gives up and
// every reconnect rejoins and re-fetches (#365). A page that has learned the
// session isn't the teacher's stops there, so it doesn't send 403s to the
// backend on every reconnect.

const _sessionId = 'aaaaaaaa-0000-0000-0000-000000000382';
const _notYours =
    "This session isn't available to you. Only the teacher who started it can "
    'open it.';

ApiClient _dummyClient() => ApiClient(
  baseUrl: Uri.parse('http://localhost'),
  tokenProvider: () async => null,
);

SessionDetail _detail({DateTime? endedAt}) => SessionDetail(
  id: _sessionId,
  classId: 'c1',
  className: 'Math 101',
  joinCode: 'ABC123',
  startedAt: DateTime(2026, 6, 12, 9, 15),
  endedAt: endedAt,
  summaries: const [],
  recentEvents: const [],
  participants: const [],
  bundles: const [],
  grants: const [],
);

/// The backend as #369 left it. While [forbidden], the session belongs to
/// another teacher and both endpoints the session pages load answer 403.
/// [detailGate], when set, holds the detail's answer back until completed, so
/// a test can let the unblock requests answer first.
class _Sessions extends SessionsApi {
  _Sessions({this.forbidden = true, this.endedAt, this.detailGate})
    : super(_dummyClient());

  bool forbidden;
  final DateTime? endedAt;
  final Completer<void>? detailGate;
  Object? detailError;
  int getSessionCalls = 0;
  int unblockRequestsCalls = 0;

  @override
  Future<SessionDetail> getSession(String sessionId) async {
    getSessionCalls++;
    await detailGate?.future;
    final error = detailError;
    if (error != null) throw error;
    if (forbidden) throw ApiException(403, '');
    return _detail(endedAt: endedAt);
  }

  @override
  Future<List<UnblockRequestSummary>> unblockRequests(String sessionId) async {
    unblockRequestsCalls++;
    if (forbidden) throw ApiException(403, '');
    return const [];
  }
}

class _Bundles extends BundlesApi {
  _Bundles() : super(_dummyClient());

  int listCalls = 0;

  @override
  Future<List<BundleSummary>> list({bool includeArchived = false}) async {
    listCalls++;
    return const [];
  }
}

/// Stands in for the SignalR client the way the real one behaves: once
/// connected it comes back after every drop and fires [reconnected] (#370),
/// and [disconnect] stops it for good. The hub refuses JoinSession from
/// anyone but the owning teacher (#366).
class _StubHub extends SessionHubClient {
  _StubHub(this.sessions)
    : super(apiBaseUrl: Uri.parse('http://localhost'), tokenProvider: _noToken);

  static Future<String?> _noToken() async => null;

  final _Sessions sessions;
  final _events = StreamController<SessionEvent>.broadcast();
  final _reconnectedCtrl = StreamController<void>.broadcast();
  final _linkCtrl = StreamController<SessionHubLinkState>.broadcast();
  bool _connected = false;
  bool _stopped = false;
  int connectCalls = 0;
  int joinCalls = 0;
  int restartCalls = 0;
  int disconnectCalls = 0;

  @override
  Stream<SessionEvent> get events => _events.stream;

  @override
  Stream<void> get reconnected => _reconnectedCtrl.stream;

  @override
  Stream<SessionHubLinkState> get linkState => _linkCtrl.stream;

  /// The connection drops and SignalR brings it back, as it keeps doing for
  /// as long as the page holds a connection.
  void dropAndReconnect() {
    if (!_connected || _stopped) return;
    _linkCtrl.add(SessionHubLinkState.reconnecting);
    _linkCtrl.add(SessionHubLinkState.connected);
    _reconnectedCtrl.add(null);
  }

  @override
  Future<void> connect() async {
    connectCalls++;
    _connected = true;
    _linkCtrl.add(SessionHubLinkState.connected);
  }

  @override
  Future<void> restart() async {
    restartCalls++;
  }

  @override
  Future<void> joinSession(String sessionId, {String? joinCode}) async {
    joinCalls++;
    if (sessions.forbidden) {
      throw Exception(
        "HubException: An unexpected error occurred invoking 'JoinSession' "
        'on the server. HubException: Not a participant of this session.',
      );
    }
  }

  @override
  Future<void> leaveSession(String sessionId) async {}

  @override
  Future<void> disconnect() async {
    disconnectCalls++;
    _stopped = true;
  }

  @override
  Future<void> dispose() async {
    await _events.close();
    await _reconnectedCtrl.close();
    await _linkCtrl.close();
  }
}

void _bigWindow(WidgetTester tester) {
  tester.view.physicalSize = const Size(1400, 1000);
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.resetPhysicalSize);
  addTearDown(tester.view.resetDevicePixelRatio);
}

Widget _app(Widget page) => MaterialApp(
  theme: PlinkTheme.paper,
  localizationsDelegates: AppLocalizations.localizationsDelegates,
  supportedLocales: AppLocalizations.supportedLocales,
  home: page,
);

Future<({_StubHub hub, _Sessions sessions, _Bundles bundles})> _pumpLive(
  WidgetTester tester,
  _Sessions sessions,
) async {
  _bigWindow(tester);
  final hub = _StubHub(sessions);
  final bundles = _Bundles();
  await tester.pumpWidget(
    _app(
      SessionPage(
        sessionId: _sessionId,
        tokens: AuthTokenStore(),
        sessions: sessions,
        bundles: bundles,
        apiBaseUrl: Uri.parse('http://localhost'),
        hubClientFactory: ({required apiBaseUrl, required tokenProvider}) =>
            hub,
      ),
    ),
  );
  await tester.pumpAndSettle();
  return (hub: hub, sessions: sessions, bundles: bundles);
}

/// The notice reads as the calm not-available notice of #278: ink, not the
/// error colour, and nothing of the raw exception.
void _expectCalmNotice(WidgetTester tester) {
  expect(find.text(_notYours), findsOneWidget);
  expect(
    tester.widget<Text>(find.text(_notYours)).style?.color,
    PlinkColors.ink60,
  );
  expect(find.textContaining('ApiException'), findsNothing);
  expect(find.textContaining('HubException'), findsNothing);
  expect(find.textContaining('403'), findsNothing);
  expect(find.byType(CircularProgressIndicator), findsNothing);
}

void _resumeApp(WidgetTester tester) {
  tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.inactive);
  tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.resumed);
}

void main() {
  group('past session (#382)', () {
    testWidgets(
      "another teacher's past session shows the calm notice, and neither 403 "
      'goes uncaught',
      (tester) async {
        _bigWindow(tester);
        final sessions = _Sessions();
        await tester.pumpWidget(
          _app(PastSessionPage(sessionId: _sessionId, sessions: sessions)),
        );
        await tester.pumpAndSettle();

        _expectCalmNotice(tester);
        expect(find.text('Session not available.'), findsNothing);
        expect(tester.takeException(), isNull);
      },
    );

    testWidgets(
      'the unblock requests answering 403 before the detail does is not '
      'uncaught either',
      (tester) async {
        _bigWindow(tester);
        final gate = Completer<void>();
        final sessions = _Sessions(detailGate: gate);
        await tester.pumpWidget(
          _app(PastSessionPage(sessionId: _sessionId, sessions: sessions)),
        );
        await tester.pump();
        await tester.pump();
        // Only the unblock requests have answered so far.
        expect(find.byType(CircularProgressIndicator), findsOneWidget);

        gate.complete();
        await tester.pumpAndSettle();

        _expectCalmNotice(tester);
        expect(tester.takeException(), isNull);
      },
    );

    testWidgets(
      'any other load failure reads as a human sentence, never the raw '
      'exception',
      (tester) async {
        _bigWindow(tester);
        final sessions = _Sessions(forbidden: false)
          ..detailError = ApiException(500, 'System.Exception: boom');
        await tester.pumpWidget(
          _app(PastSessionPage(sessionId: _sessionId, sessions: sessions)),
        );
        await tester.pumpAndSettle();

        expect(
          find.text('Could not load this past session. Please try again.'),
          findsOneWidget,
        );
        expect(find.textContaining('ApiException'), findsNothing);
        expect(find.textContaining('boom'), findsNothing);
        expect(find.text(_notYours), findsNothing);
      },
    );

    testWidgets("the owner's past session still renders", (tester) async {
      _bigWindow(tester);
      final sessions = _Sessions(
        forbidden: false,
        endedAt: DateTime(2026, 6, 12, 10, 5),
      );
      await tester.pumpWidget(
        _app(PastSessionPage(sessionId: _sessionId, sessions: sessions)),
      );
      await tester.pumpAndSettle();

      expect(find.text('Math 101'), findsOneWidget);
      expect(find.text(_notYours), findsNothing);
    });
  });

  group('live session (#382)', () {
    testWidgets(
      "another teacher's live session shows the calm notice and never opens "
      'the hub',
      (tester) async {
        final h = await _pumpLive(tester, _Sessions());

        _expectCalmNotice(tester);
        expect(find.textContaining('Could not connect'), findsNothing);
        // None of the owner's controls: no LIVE badge, no End, no leave
        // dialog that offers to end a session that isn't theirs.
        expect(find.text('LIVE'), findsNothing);
        expect(find.text('End session'), findsNothing);
        expect(find.byIcon(Icons.arrow_back), findsNothing);

        // The detail answered 403, so the page asked for nothing else: no
        // hub connection, no join the hub would refuse, no unblock requests
        // or bundles for a session it can't show.
        expect(h.sessions.getSessionCalls, 1);
        expect(h.sessions.unblockRequestsCalls, 0);
        expect(h.bundles.listCalls, 0);
        expect(h.hub.connectCalls, 0);
        expect(h.hub.joinCalls, 0);
        expect(tester.takeException(), isNull);
      },
    );

    testWidgets(
      'coming back to the page does not start a hub connection for it',
      (tester) async {
        final h = await _pumpLive(tester, _Sessions());

        for (var i = 0; i < 3; i++) {
          _resumeApp(tester);
          await tester.pumpAndSettle();
        }

        expect(h.hub.restartCalls, 0);
        expect(h.hub.connectCalls, 0);
        expect(h.hub.joinCalls, 0);
        expect(h.sessions.getSessionCalls, 1);
        _expectCalmNotice(tester);
      },
    );

    testWidgets(
      'a 403 on the re-fetch after a reconnect stops the hub instead of '
      'rejoining on every reconnect (#365, #370)',
      (tester) async {
        // The page opened as the owner, so it is connected and joined.
        final h = await _pumpLive(tester, _Sessions(forbidden: false));
        expect(h.hub.joinCalls, 1);
        expect(find.text('LIVE'), findsOneWidget);

        // From here on the backend answers 403 (e.g. the page stayed open
        // across the #369 deploy). The next reconnect rejoins, which the hub
        // refuses, and re-fetches, which answers 403.
        h.sessions.forbidden = true;
        h.hub.dropAndReconnect();
        await tester.pumpAndSettle();

        _expectCalmNotice(tester);
        expect(h.hub.disconnectCalls, 1);
        final joins = h.hub.joinCalls;
        final details = h.sessions.getSessionCalls;
        final pending = h.sessions.unblockRequestsCalls;

        // No more reconnects, rejoins or re-fetches: the connection is
        // stopped, and coming back to the page doesn't restart it.
        for (var i = 0; i < 5; i++) {
          h.hub.dropAndReconnect();
          _resumeApp(tester);
          await tester.pump(const Duration(seconds: 30));
        }
        await tester.pumpAndSettle();

        expect(h.hub.joinCalls, joins);
        expect(h.sessions.getSessionCalls, details);
        expect(h.sessions.unblockRequestsCalls, pending);
        expect(h.hub.restartCalls, 0);
        _expectCalmNotice(tester);
        expect(tester.takeException(), isNull);
      },
    );

    testWidgets("the owner's live session still connects and joins", (
      tester,
    ) async {
      final h = await _pumpLive(tester, _Sessions(forbidden: false));

      expect(find.text('LIVE'), findsOneWidget);
      expect(find.text(_notYours), findsNothing);
      expect(h.hub.connectCalls, 1);
      expect(h.hub.joinCalls, 1);
      expect(h.sessions.unblockRequestsCalls, 1);
      expect(h.hub.disconnectCalls, 0);
    });
  });
}
