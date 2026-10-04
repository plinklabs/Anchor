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
import 'package:http/http.dart' as http;
import 'package:plink_design_system/plink_design_system.dart';

// #383: when one of the owning teacher's own actions on the live page fails,
// the page says so in a sentence, never the raw exception. Before, the four
// error lines carried `'$e'`: `Failed to end session: ApiException(500): …`,
// `Could not connect to live stream: Exception: An unexpected error occurred
// invoking 'JoinSession' on the server. HubException: …`. Now each goes through
// describeApiError, the way Home, Classes and History do (#278).
//
// A 403 from an action means what it means on the loads (#382): the backend
// refuses this session to the signed-in teacher (#369). The page shows the
// calm not-yours notice in place of the live view and stops the hub, rather
// than an error line under controls that would all answer 403 too.

const _sessionId = 'aaaaaaaa-0000-0000-0000-000000000383';
const _host = 'chat.example.com';

const _connectError =
    'Could not connect to the live feed for this session. Reload the page to '
    'try again.';
const _endError = 'Failed to end session. Please try again.';
const _approveError = 'Could not approve the request. Please try again.';
const _bundlesError = 'Could not update the allowed bundles. Please try again.';
const _notYours =
    "This session isn't available to you. Only the teacher who started it can "
    'open it.';

ApiClient _dummyClient() => ApiClient(
  baseUrl: Uri.parse('http://localhost'),
  tokenProvider: () async => null,
);

/// What the browser's http client throws when the request never reaches the
/// backend: offline, or the backend is down.
http.ClientException _offline() => http.ClientException(
  'Failed to fetch',
  Uri.parse('http://localhost/sessions/$_sessionId'),
);

/// What the real SignalR client throws when the hub refuses JoinSession with
/// a HubException: signalr_core wraps the server's message in an Exception.
Exception _hubRefused() => Exception(
  "An unexpected error occurred invoking 'JoinSession' on the server. "
  'HubException: Session not found or already ended.',
);

/// What it throws when the connection goes while the invocation runs.
Exception _hubConnectionGone() => Exception(
  'Invocation canceled due to the underlying connection being closed.',
);

final _at = DateTime(2026, 6, 12, 9, 20);

UnblockRequestSummary _pending() => UnblockRequestSummary(
  host: _host,
  count: 1,
  firstRequestedAt: _at,
  latestRequestedAt: _at,
  requesters: [
    UnblockRequestRequester(userId: 'u1', displayName: 'Ada', requestedAt: _at),
  ],
);

/// The owner's session. Each action throws what a test sets.
class _Sessions extends SessionsApi {
  _Sessions() : super(_dummyClient());

  Object? endError;
  Object? approveError;
  Object? approveClassError;
  Object? updateBundlesError;
  int endCalls = 0;

  @override
  Future<SessionDetail> getSession(String sessionId) async => SessionDetail(
    id: sessionId,
    classId: 'c1',
    className: 'Math 101',
    joinCode: 'ABC123',
    startedAt: DateTime(2026, 6, 12, 9, 15),
    endedAt: null,
    summaries: const [],
    recentEvents: const [],
    participants: const [],
    bundles: const [],
    grants: const [],
  );

  @override
  Future<List<UnblockRequestSummary>> unblockRequests(String sessionId) async =>
      [_pending()];

  @override
  Future<void> endSession(String sessionId) async {
    endCalls++;
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
  }

  @override
  Future<void> approveUnblockForClass(String sessionId, String host) async {
    final error = approveClassError;
    if (error != null) throw error;
  }

  @override
  Future<List<SessionBundleInfo>> updateBundles(
    String sessionId,
    List<String> bundleIds,
  ) async {
    final error = updateBundlesError;
    if (error != null) throw error;
    return [for (final id in bundleIds) SessionBundleInfo(id: id, name: id)];
  }
}

class _Bundles extends BundlesApi {
  _Bundles() : super(_dummyClient());

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

/// Stands in for the SignalR client: it connects, and JoinSession throws
/// [joinError] when a test sets one.
class _StubHub extends SessionHubClient {
  _StubHub()
    : super(apiBaseUrl: Uri.parse('http://localhost'), tokenProvider: _noToken);

  static Future<String?> _noToken() async => null;

  final _events = StreamController<SessionEvent>.broadcast();
  final _reconnectedCtrl = StreamController<void>.broadcast();
  final _linkCtrl = StreamController<SessionHubLinkState>.broadcast();
  Object? joinError;
  int disconnectCalls = 0;

  @override
  Stream<SessionEvent> get events => _events.stream;

  @override
  Stream<void> get reconnected => _reconnectedCtrl.stream;

  @override
  Stream<SessionHubLinkState> get linkState => _linkCtrl.stream;

  @override
  Future<void> connect() async {
    _linkCtrl.add(SessionHubLinkState.connected);
  }

  @override
  Future<void> restart() async {}

  @override
  Future<void> joinSession(String sessionId, {String? joinCode}) async {
    final error = joinError;
    if (error != null) throw error;
  }

  @override
  Future<void> leaveSession(String sessionId) async {}

  @override
  Future<void> disconnect() async {
    disconnectCalls++;
  }

  @override
  Future<void> dispose() async {
    await _events.close();
    await _reconnectedCtrl.close();
    await _linkCtrl.close();
  }
}

Future<({_StubHub hub, _Sessions sessions})> _pumpLive(
  WidgetTester tester, {
  Object? joinError,
}) async {
  // Room for the pending-requests and bundles panels under the roster.
  tester.view.physicalSize = const Size(1400, 1000);
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.resetPhysicalSize);
  addTearDown(tester.view.resetDevicePixelRatio);

  final hub = _StubHub()..joinError = joinError;
  final sessions = _Sessions();
  await tester.pumpWidget(
    MaterialApp(
      // NoSplash: the ink sparkle's shader doesn't decode under the test
      // engine when a tap ripples.
      theme: PlinkTheme.paper.copyWith(splashFactory: NoSplash.splashFactory),
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      home: SessionPage(
        sessionId: _sessionId,
        tokens: AuthTokenStore(),
        sessions: sessions,
        bundles: _Bundles(),
        apiBaseUrl: Uri.parse('http://localhost'),
        hubClientFactory: ({required apiBaseUrl, required tokenProvider}) =>
            hub,
      ),
    ),
  );
  await tester.pumpAndSettle();
  return (hub: hub, sessions: sessions);
}

Future<void> _tapAndSettle(WidgetTester tester, Finder finder) async {
  await tester.tap(finder);
  await tester.pumpAndSettle();
}

Future<void> _endFromHeader(WidgetTester tester) =>
    _tapAndSettle(tester, find.widgetWithText(OutlinedButton, 'End session'));

Future<void> _endFromLeaveDialog(WidgetTester tester) async {
  await _tapAndSettle(tester, find.byIcon(Icons.arrow_back));
  await _tapAndSettle(
    tester,
    find.descendant(
      of: find.byType(AlertDialog),
      matching: find.widgetWithText(FilledButton, 'End session'),
    ),
  );
}

Future<void> _approveForStudent(WidgetTester tester) =>
    _tapAndSettle(tester, find.widgetWithText(OutlinedButton, 'Approve'));

Future<void> _approveForClass(WidgetTester tester) async {
  await _tapAndSettle(tester, find.byIcon(Icons.more_vert));
  await _tapAndSettle(tester, find.text('Approve for whole class'));
}

Future<void> _toggleBundle(WidgetTester tester) =>
    _tapAndSettle(tester, find.widgetWithText(FilterChip, 'Math'));

/// [sentence] is on the page in the error colour, and nothing of the raw
/// exception is.
void _expectSentence(WidgetTester tester, String sentence) {
  expect(find.text(sentence), findsOneWidget);
  final context = tester.element(find.byType(SessionPage));
  expect(
    tester.widget<Text>(find.text(sentence)).style?.color,
    Theme.of(context).colorScheme.error,
  );
  _expectNoRawError();
}

void _expectNoRawError() {
  for (final raw in const [
    'ApiException',
    'HubException',
    'Exception',
    'ClientException',
    'Failed to fetch',
    'JoinSession',
    'Invocation canceled',
    'System.',
    '500',
  ]) {
    expect(find.textContaining(raw), findsNothing, reason: raw);
  }
}

void main() {
  group('a failed action of the owner reads as a sentence (#383)', () {
    for (final (name, error) in [
      ('a HubException', _hubRefused()),
      ('a dropped connection', _hubConnectionGone()),
    ]) {
      testWidgets('JoinSession refused with $name', (tester) async {
        await _pumpLive(tester, joinError: error);

        _expectSentence(tester, _connectError);
        // Still the owner's live view: the join failing doesn't hide it.
        expect(find.text('End session'), findsOneWidget);
        expect(find.text(_notYours), findsNothing);
      });
    }

    testWidgets('End session answering 500', (tester) async {
      final h = await _pumpLive(tester);
      h.sessions.endError = ApiException(500, 'System.Exception: boom');

      await _endFromHeader(tester);

      expect(h.sessions.endCalls, 1);
      _expectSentence(tester, _endError);
      expect(find.text('LIVE'), findsOneWidget);
    });

    testWidgets('End session from the leave dialog while offline', (
      tester,
    ) async {
      final h = await _pumpLive(tester);
      h.sessions.endError = _offline();

      await _endFromLeaveDialog(tester);

      expect(h.sessions.endCalls, 1);
      expect(find.byType(AlertDialog), findsNothing);
      _expectSentence(tester, _endError);
    });

    testWidgets('Approve while offline', (tester) async {
      final h = await _pumpLive(tester);
      h.sessions.approveError = _offline();

      await _approveForStudent(tester);

      _expectSentence(tester, _approveError);
      // The request is still there to approve again.
      expect(find.text(_host), findsOneWidget);
    });

    testWidgets('Approve for whole class answering 500', (tester) async {
      final h = await _pumpLive(tester);
      h.sessions.approveClassError = ApiException(500, 'System.Exception');

      await _approveForClass(tester);

      _expectSentence(tester, _approveError);
    });

    testWidgets('changing the allowed bundles answering 500', (tester) async {
      final h = await _pumpLive(tester);
      h.sessions.updateBundlesError = ApiException(500, 'System.Exception');

      await _toggleBundle(tester);

      _expectSentence(tester, _bundlesError);
    });

    testWidgets('changing the allowed bundles while offline', (tester) async {
      final h = await _pumpLive(tester);
      h.sessions.updateBundlesError = _offline();

      await _toggleBundle(tester);

      _expectSentence(tester, _bundlesError);
    });
  });

  group('a 403 on an action is the not-yours notice, as on the loads', () {
    for (final (name, fail, act)
        in <
          (
            String,
            void Function(_Sessions),
            Future<void> Function(WidgetTester),
          )
        >[
          (
            'End session',
            (s) => s.endError = ApiException(403, ''),
            _endFromHeader,
          ),
          (
            'End session from the leave dialog',
            (s) => s.endError = ApiException(403, ''),
            _endFromLeaveDialog,
          ),
          (
            'Approve',
            (s) => s.approveError = ApiException(403, ''),
            _approveForStudent,
          ),
          (
            'Approve for whole class',
            (s) => s.approveClassError = ApiException(403, ''),
            _approveForClass,
          ),
          (
            'changing the allowed bundles',
            (s) => s.updateBundlesError = ApiException(403, ''),
            _toggleBundle,
          ),
        ]) {
      testWidgets('$name answering 403 (#382, #383)', (tester) async {
        final h = await _pumpLive(tester);
        fail(h.sessions);

        await act(tester);

        expect(find.text(_notYours), findsOneWidget);
        expect(
          tester.widget<Text>(find.text(_notYours)).style?.color,
          PlinkColors.ink60,
        );
        // None of the owner's controls, and no error line under them.
        expect(find.text('End session'), findsNothing);
        expect(find.text(_host), findsNothing);
        for (final line in const [_endError, _approveError, _bundlesError]) {
          expect(find.text(line), findsNothing);
        }
        _expectNoRawError();
        expect(find.textContaining('403'), findsNothing);
        // The hub stops, so its reconnects don't keep asking (#382).
        expect(h.hub.disconnectCalls, 1);
      });
    }
  });
}
