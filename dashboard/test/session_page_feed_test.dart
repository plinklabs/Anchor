import 'dart:async';

import 'package:anchor_dashboard/alerts/alert_platform.dart';
import 'package:anchor_dashboard/alerts/live_alert_services.dart';
import 'package:anchor_dashboard/alerts/live_alert_settings.dart';
import 'package:anchor_dashboard/alerts/live_alerts.dart';
import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/auth_token_store.dart';
import 'package:anchor_dashboard/api/bundles_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/feed/feed_history.dart';
import 'package:anchor_dashboard/l10n/app_localizations.dart';
import 'package:anchor_dashboard/pages/session_page.dart';
import 'package:anchor_dashboard/realtime/session_hub_client.dart';
import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

// #491: the live page's activity feed reads as plain sentences, and by
// default shows only what needs the teacher's attention; a switch on its
// header shows everything, and the browser remembers it. These pump the page
// alone; integration_test/live_session_test.dart drives the same through the
// real app, and live_feed_test.dart pins every sentence.

const _sessionId = '5fbae478-0000-0000-0000-000000000491';
const _adaId = 'edee9562-0000-0000-0000-00000000000a';
const _boId = 'edee9562-0000-0000-0000-00000000000b';
const _waiting = 'Waiting for events…';
const _nothing = 'Nothing that needs your attention yet';
const _showAll = 'Show all activity';

ApiClient _dummyClient() => ApiClient(
  baseUrl: Uri.parse('http://localhost'),
  tokenProvider: () async => null,
);

class _StubHub extends SessionHubClient {
  _StubHub({List<String>? log})
    : _log = log,
      super(apiBaseUrl: Uri.parse('http://localhost'), tokenProvider: _noToken);

  static Future<String?> _noToken() async => null;

  final _ctrl = StreamController<SessionEvent>.broadcast();

  /// Where the page's join is noted, next to its loads (#492).
  final List<String>? _log;

  void emit(String kind, Map<String, dynamic> payload, {DateTime? at}) =>
      _ctrl.add(
        SessionEvent(
          kind: kind,
          payload: {'sessionId': _sessionId, ...payload},
          at: at ?? DateTime.now(),
        ),
      );

  void state(String id, String name, String state) =>
      emit('ParticipantStateChanged', {
        'userId': id,
        'displayName': name,
        'state': state,
        'at': '2026-11-11T19:28:11.3797761+00:00',
      });

  @override
  Stream<SessionEvent> get events => _ctrl.stream;

  @override
  Future<void> connect() async {}
  @override
  Future<void> joinSession(String sessionId, {String? joinCode}) async =>
      _log?.add('join');
  @override
  Future<void> leaveSession(String sessionId) async {}
  @override
  Future<void> disconnect() async {}
  @override
  Future<void> dispose() async => _ctrl.close();
}

SessionParticipantInfo _participant(
  String id,
  String name, {
  DateTime? joinedAt,
  DateTime? leftAt,
}) => SessionParticipantInfo(
  userId: id,
  displayName: name,
  joinedAt: joinedAt,
  declinedAt: null,
  leftAt: leftAt,
  state: leftAt == null
      ? ParticipantLiveState.joined
      : ParticipantLiveState.left,
);

class _FakeSessions extends SessionsApi {
  _FakeSessions({
    this.participants,
    this.stored = const [],
    this.eventsError,
    this.whileLoading,
  }) : super(_dummyClient());

  /// The roster; Ada and Bo, joined, when null.
  final List<SessionParticipantInfo>? participants;

  /// What the backend stored for the session (#492), newest first.
  final List<SessionRecentEvent> stored;

  /// What loading them throws, when set.
  final Object? eventsError;

  /// What happens while the page loads them: an event the hub broadcasts
  /// between the page's join and the load (#492).
  final void Function(_StubHub hub)? whileLoading;

  /// The page's hub, for [whileLoading].
  _StubHub? hub;

  /// The page's join and loads, in order.
  final List<String> log = [];
  Iterable<String>? askedKinds;
  int? askedLimit;

  @override
  Future<SessionDetail> getSession(String sessionId) async => SessionDetail(
    id: sessionId,
    classId: 'c1',
    className: 'Math 101',
    joinCode: '019480',
    startedAt: DateTime(2026, 11, 11, 20, 25),
    endedAt: null,
    summaries: const [],
    recentEvents: const [],
    participants:
        participants ??
        [_participant(_adaId, 'Ada'), _participant(_boId, 'Bo')],
    bundles: const [],
    grants: const [],
  );

  @override
  Future<List<UnblockRequestSummary>> unblockRequests(String sessionId) async =>
      const [];

  @override
  Future<List<SessionRecentEvent>> sessionEvents(
    String sessionId, {
    Iterable<String> kinds = const <String>[],
    int limit = 200,
  }) async {
    log.add('events');
    askedKinds = kinds;
    askedLimit = limit;
    whileLoading?.call(hub!);
    final error = eventsError;
    if (error != null) throw error;
    return stored;
  }
}

class _FakeBundles extends BundlesApi {
  _FakeBundles() : super(_dummyClient());

  @override
  Future<List<BundleSummary>> list({bool includeArchived = false}) async =>
      const [];
}

Future<_StubHub> _pump(
  WidgetTester tester,
  LiveAlertSettings settings, {
  Locale locale = const Locale('en'),
  _FakeSessions? sessions,
}) async {
  tester.view.physicalSize = const Size(1400, 1000);
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.resetPhysicalSize);
  addTearDown(tester.view.resetDevicePixelRatio);

  final hub = _StubHub(log: sessions?.log);
  sessions?.hub = hub;
  await tester.pumpWidget(
    MaterialApp(
      // NoSplash avoids loading the ink_sparkle fragment shader, which fails
      // to decode under the test engine when a tap triggers a ripple.
      theme: ThemeData(splashFactory: NoSplash.splashFactory),
      locale: locale,
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      home: SessionPage(
        sessionId: _sessionId,
        tokens: AuthTokenStore(),
        sessions: sessions ?? _FakeSessions(),
        bundles: _FakeBundles(),
        apiBaseUrl: Uri.parse('http://localhost'),
        hubClientFactory: ({required apiBaseUrl, required tokenProvider}) =>
            hub,
        alertServices: LiveAlertServices(
          settings: settings,
          sound: createAlertSound(),
        ),
      ),
    ),
  );
  await tester.pumpAndSettle();
  return hub;
}

/// The colour of the status dot on the feed row that reads [sentence].
Color? _dotOf(WidgetTester tester, String sentence) {
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

void main() {
  testWidgets('by default the feed shows only what needs the teacher, as '
      'sentences, and says so while nothing does', (tester) async {
    final hub = await _pump(tester, LiveAlertSettings());
    expect(find.text(_waiting), findsOneWidget);
    expect(find.text(_showAll), findsOneWidget);

    // The session starting and students joining don't need the teacher.
    hub.emit('SessionStarted', {
      'joinCode': '019480',
      'apps': const [],
      'domains': const [],
    });
    hub.state(_adaId, 'Ada', 'Joined');
    hub.state(_boId, 'Bo', 'Joined');
    await tester.pumpAndSettle();
    expect(find.text(_waiting), findsNothing);
    expect(find.text(_nothing), findsOneWidget);
    expect(find.text('Ada joined.'), findsNothing);

    // What does.
    hub.state(_boId, 'Bo', 'Left');
    hub.emit('UnblockRequested', {
      'userId': _adaId,
      'userDisplayName': 'Ada',
      'host': 'example.com',
    });
    hub.emit('HeartbeatLost', {'userId': _adaId});
    hub.emit('TamperDetected', {
      'userId': _boId,
      'userDisplayName': 'Bo',
      'kind': 'extension_disabled',
    });
    hub.state(_adaId, 'Ada', 'AgentMissing');
    await tester.pumpAndSettle();

    expect(find.text(_nothing), findsNothing);
    expect(find.text('Bo left the session.'), findsOneWidget);
    expect(find.text('Ada asks for access to example.com.'), findsOneWidget);
    // Named from the roster: the event carries only the id.
    expect(find.text("Ada's agent stopped reporting."), findsOneWidget);
    expect(
      find.text("Bo's Anchor extension was switched off or removed."),
      findsOneWidget,
    );
    expect(find.text("Ada's agent hasn't reported in."), findsOneWidget);
    expect(find.text('Ada joined.'), findsNothing);

    // No raw kind, id or UTC timestamp anywhere on the page.
    for (final raw in [
      'ParticipantStateChanged',
      'HeartbeatLost',
      'TamperDetected',
      _sessionId,
      _adaId,
      '+00:00',
      'state=',
    ]) {
      expect(find.textContaining(raw), findsNothing, reason: raw);
    }

    // The stronger marker where a laptop isn't enforced or was tampered with.
    final error = Theme.of(
      tester.element(find.text(_showAll)),
    ).colorScheme.error;
    expect(_dotOf(tester, "Ada's agent stopped reporting."), error);
    expect(_dotOf(tester, "Ada's agent hasn't reported in."), error);
    expect(
      _dotOf(tester, "Bo's Anchor extension was switched off or removed."),
      error,
    );
    expect(_dotOf(tester, 'Bo left the session.'), isNot(error));
    expect(_dotOf(tester, 'Ada asks for access to example.com.'), isNot(error));
    expect(tester.takeException(), isNull);
  });

  testWidgets('the switch shows all activity, and the browser remembers it', (
    tester,
  ) async {
    final store = MemoryAlertSettingsStore();
    final hub = await _pump(tester, LiveAlertSettings(store: store));
    hub.emit('SessionStarted', {
      'joinCode': '019480',
      'apps': [
        {'matchKind': 'ProcessName', 'value': 'msedge'},
        {'matchKind': 'ProcessName', 'value': 'code'},
      ],
      'domains': [
        for (var i = 0; i < 10; i++)
          {'matchType': 'Wildcard', 'value': 'site$i.example'},
      ],
    });
    hub.state(_adaId, 'Ada', 'Joined');
    hub.state(_boId, 'Bo', 'Left');
    await tester.pumpAndSettle();
    expect(find.text('Ada joined.'), findsNothing);
    expect(find.text('Bo left the session.'), findsOneWidget);

    await tester.tap(find.byKey(const Key('feed-show-all')));
    await tester.pumpAndSettle();

    expect(find.text('Bo left the session.'), findsOneWidget);
    expect(find.text('Ada joined.'), findsOneWidget);
    expect(
      find.text(
        'Session started, code 019480: 2 apps and 10 websites allowed.',
      ),
      findsOneWidget,
    );
    // Newest first, as it came.
    expect(
      tester.getTopLeft(find.text('Bo left the session.')).dy,
      lessThan(tester.getTopLeft(find.text('Ada joined.')).dy),
    );
    expect(store.value, contains('"showAllActivity":true'));

    // A reload: a new page, with what the browser kept.
    await tester.pumpWidget(const SizedBox.shrink());
    final again = await _pump(tester, LiveAlertSettings(store: store));
    expect(
      tester.widget<Switch>(find.byKey(const Key('feed-show-all'))).value,
      isTrue,
    );
    again.state(_adaId, 'Ada', 'Joined');
    await tester.pumpAndSettle();
    expect(find.text('Ada joined.'), findsOneWidget);

    // Off again, the joins go.
    await tester.tap(find.byKey(const Key('feed-show-all')));
    await tester.pumpAndSettle();
    expect(find.text('Ada joined.'), findsNothing);
    expect(find.text(_nothing), findsOneWidget);
    expect(store.value, contains('"showAllActivity":false'));
  });

  testWidgets("the feed follows what needs attention, not the teacher's "
      'alert switches', (tester) async {
    // The teacher switched every alert off: nothing interrupts them, but
    // the page still shows what needs them.
    final settings = LiveAlertSettings();
    for (final category in LiveAlertCategory.values) {
      settings.setOn(category, false);
    }
    final hub = await _pump(tester, settings);

    hub.state(_boId, 'Bo', 'Declined');
    hub.emit('HeartbeatLost', {'userId': _adaId});
    await tester.pumpAndSettle();

    expect(find.text('Bo declined the session.'), findsOneWidget);
    expect(find.text("Ada's agent stopped reporting."), findsOneWidget);
  });

  testWidgets('the same sentence about a student in a row is one row, with '
      'its count', (tester) async {
    final hub = await _pump(tester, LiveAlertSettings());

    // A student who keeps reconnecting: the joins between are hidden.
    hub.state(_boId, 'Bo', 'Left');
    hub.state(_boId, 'Bo', 'Joined');
    hub.state(_boId, 'Bo', 'Left');
    hub.state(_boId, 'Bo', 'Joined');
    hub.state(_boId, 'Bo', 'Left');
    await tester.pumpAndSettle();

    expect(find.text('Bo left the session.'), findsOneWidget);
    expect(find.text('×3'), findsOneWidget);

    await tester.tap(find.byKey(const Key('feed-show-all')));
    await tester.pumpAndSettle();
    expect(find.text('Bo left the session.'), findsNWidgets(3));
    expect(find.text('×3'), findsNothing);
  });

  testWidgets('the time is local, and the raw event waits behind a '
      'lingering hover', (tester) async {
    final hub = await _pump(tester, LiveAlertSettings());
    final at = DateTime.utc(2026, 11, 11, 19, 28, 11);
    hub.emit('ParticipantStateChanged', {
      'userId': _boId,
      'displayName': 'Bo',
      'state': 'Left',
      'at': '2026-11-11T19:28:11.3797761+00:00',
    }, at: at);
    await tester.pumpAndSettle();

    final local = at.toLocal();
    String two(int n) => n.toString().padLeft(2, '0');
    final time = '${two(local.hour)}:${two(local.minute)}:${two(local.second)}';
    expect(find.text(time), findsOneWidget);

    // For troubleshooting, out of the teacher's way.
    final mouse = await tester.createGesture(kind: PointerDeviceKind.mouse);
    await mouse.addPointer(location: Offset.zero);
    addTearDown(mouse.removePointer);
    await mouse.moveTo(tester.getCenter(find.text('Bo left the session.')));
    await tester.pump(const Duration(milliseconds: 300));
    expect(find.textContaining('ParticipantStateChanged'), findsNothing);
    await tester.pump(const Duration(seconds: 1));
    await tester.pumpAndSettle();
    expect(find.textContaining('ParticipantStateChanged'), findsOneWidget);
    expect(find.textContaining('state: Left'), findsOneWidget);
    await mouse.moveTo(Offset.zero);
    await tester.pumpAndSettle();
  });

  testWidgets('in Dutch', (tester) async {
    final hub = await _pump(
      tester,
      LiveAlertSettings(),
      locale: const Locale('nl'),
    );
    expect(find.text('Alle activiteit tonen'), findsOneWidget);

    hub.state(_adaId, 'Ada', 'Joined');
    await tester.pumpAndSettle();
    expect(find.text('Nog niets dat je aandacht vraagt'), findsOneWidget);

    hub.state(_boId, 'Bo', 'Left');
    hub.emit('TamperDetected', {
      'userId': _adaId,
      'userDisplayName': 'Ada',
      'kind': 'extension_silent',
    });
    await tester.pumpAndSettle();
    expect(find.text('Bo heeft de sessie verlaten.'), findsOneWidget);
    expect(
      find.text('De Anchor-extensie van Ada staat uit of reageert niet.'),
      findsOneWidget,
    );
    expect(tester.takeException(), isNull);
  });

  group('#492: what happened before the page opened', () {
    final start = DateTime.utc(2026, 11, 11, 19, 25);
    DateTime min(int m, [int ms = 0]) =>
        start.add(Duration(minutes: m, milliseconds: ms));
    SessionRecentEvent stored(
      String kind,
      DateTime at, {
      String userId = _adaId,
      String payloadJson = '{}',
    }) => SessionRecentEvent(
      id: '$kind$at',
      userId: userId,
      kind: kind,
      payloadJson: payloadJson,
      occurredAt: at,
    );
    String local(DateTime at) {
      final t = at.toLocal();
      String two(int n) => n.toString().padLeft(2, '0');
      return '${two(t.hour)}:${two(t.minute)}:${two(t.second)}';
    }

    double top(WidgetTester tester, String text) =>
        tester.getTopLeft(find.text(text)).dy;

    testWidgets('the feed shows it once the page has joined the session, '
        'newest first, as the live feed would have', (tester) async {
      final sessions = _FakeSessions(
        participants: [
          _participant(_adaId, 'Ada', joinedAt: min(1)),
          _participant(_boId, 'Bo', joinedAt: min(2), leftAt: min(6, 300)),
        ],
        stored: [
          stored(
            'TamperDetected',
            min(8),
            payloadJson: '{"kind":"extension_silent"}',
          ),
          stored('ManualLeave', min(6), userId: _boId),
          stored(
            'UnblockRequest',
            min(4),
            payloadJson:
                '{"url":"https://chat.example.com/","host":"chat.example.com"}',
          ),
          stored('HeartbeatLost', min(3)),
        ],
      );
      await _pump(tester, LiveAlertSettings(), sessions: sessions);

      // Loaded after the join: an event before it is in what it loads.
      expect(sessions.log, ['join', 'events']);
      expect(sessions.askedKinds, feedStoredKinds);
      expect(sessions.askedLimit, feedHistoryLimit);

      const tamper = "Ada's Anchor extension is off or not responding.";
      const left = 'Bo left the session.';
      const asks = 'Ada asks for access to chat.example.com.';
      const lost = "Ada's agent stopped reporting.";
      expect(find.text(_waiting), findsNothing);
      for (final sentence in [tamper, left, asks, lost]) {
        expect(find.text(sentence), findsOneWidget, reason: sentence);
      }
      expect(top(tester, tamper), lessThan(top(tester, left)));
      expect(top(tester, left), lessThan(top(tester, asks)));
      expect(top(tester, asks), lessThan(top(tester, lost)));
      // At the time it happened, the server's (the leave as it recorded it),
      // in local time.
      expect(find.text(local(min(8))), findsOneWidget);
      expect(find.text(local(min(6, 300))), findsOneWidget);
      // The joins only with all activity, the first one last.
      expect(find.text('Ada joined.'), findsNothing);

      await tester.tap(find.byKey(const Key('feed-show-all')));
      await tester.pumpAndSettle();
      expect(find.text('Bo joined.'), findsOneWidget);
      expect(find.text('Ada joined.'), findsOneWidget);
      expect(top(tester, lost), lessThan(top(tester, 'Bo joined.')));
      expect(top(tester, 'Bo joined.'), lessThan(top(tester, 'Ada joined.')));
      // The page hears the session starting only when the teacher takes part
      // (#410): the header says when it started.
      expect(find.textContaining('Session started'), findsNothing);
      expect(find.textContaining('ManualLeave'), findsNothing);
      expect(tester.takeException(), isNull);
    });

    testWidgets('it continues live above it, and an event the page both hears '
        'and loads shows once', (tester) async {
      final sessions = _FakeSessions(
        stored: [
          stored(
            'UnblockRequest',
            DateTime.parse('2026-11-11T19:30:00.1234567+00:00'),
            payloadJson: '{"host":"chat.example.com"}',
          ),
          stored('ManualLeave', min(2), userId: _boId),
        ],
        // Between the join and the load: broadcast, and stored too.
        whileLoading: (hub) => hub.emit('UnblockRequested', {
          'userId': _adaId,
          'userDisplayName': 'Ada',
          'host': 'chat.example.com',
          'url': 'https://chat.example.com/',
          'requestedAt': '2026-11-11T19:30:00.1234567+00:00',
        }),
      );
      final hub = await _pump(tester, LiveAlertSettings(), sessions: sessions);

      expect(
        find.text('Ada asks for access to chat.example.com.'),
        findsOneWidget,
      );
      expect(find.textContaining('×'), findsNothing);
      expect(find.text('Bo left the session.'), findsOneWidget);

      hub.emit('TamperDetected', {
        'userId': _boId,
        'userDisplayName': 'Bo',
        'kind': 'inprivate_opened',
        'detectedAt': '2026-11-11T19:31:00+00:00',
      });
      await tester.pumpAndSettle();
      expect(
        top(tester, 'Bo opened an InPrivate window.'),
        lessThan(top(tester, 'Ada asks for access to chat.example.com.')),
      );
      expect(tester.takeException(), isNull);
    });

    testWidgets('without it, the feed shows what the page hears, as before', (
      tester,
    ) async {
      final sessions = _FakeSessions(
        stored: [stored('ManualLeave', min(2), userId: _boId)],
        eventsError: ApiException(500, 'boom'),
      );
      final hub = await _pump(tester, LiveAlertSettings(), sessions: sessions);

      expect(find.text(_waiting), findsOneWidget);
      expect(find.textContaining('boom'), findsNothing);
      hub.state(_adaId, 'Ada', 'Left');
      await tester.pumpAndSettle();
      expect(find.text('Ada left the session.'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });
  });
}
