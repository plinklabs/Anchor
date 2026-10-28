import 'dart:async';

import 'package:anchor_dashboard/alerts/alert_sound.dart';
import 'package:anchor_dashboard/alerts/live_alert_services.dart';
import 'package:anchor_dashboard/alerts/live_alert_settings.dart';
import 'package:anchor_dashboard/alerts/live_alerts.dart';
import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/auth_token_store.dart';
import 'package:anchor_dashboard/api/bundles_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/l10n/app_localizations.dart';
import 'package:anchor_dashboard/pages/session_page.dart';
import 'package:anchor_dashboard/realtime/session_hub_client.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';

// #454: the live page plays a sound and counts alerts in the tab title when a
// session needs the teacher, while its tab is in the background too. These
// pump the page alone; integration_test/live_session_alerts_test.dart drives
// the same through the real app.

const _sessionId = 'aaaaaaaa-0000-0000-0000-000000000001';
const _blockedLine = 'Click anywhere to turn on sound.';

ApiClient _dummyClient() => ApiClient(
  baseUrl: Uri.parse('http://localhost'),
  tokenProvider: () async => null,
);

class _StubHub extends SessionHubClient {
  _StubHub()
    : super(apiBaseUrl: Uri.parse('http://localhost'), tokenProvider: _noToken);

  static Future<String?> _noToken() async => null;

  final _ctrl = StreamController<SessionEvent>.broadcast();

  void emit(String kind, Map<String, dynamic> payload) =>
      _ctrl.add(SessionEvent(kind: kind, payload: payload, at: DateTime.now()));

  @override
  Stream<SessionEvent> get events => _ctrl.stream;

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
      const [];
}

class _FakeBundles extends BundlesApi {
  _FakeBundles() : super(_dummyClient());

  @override
  Future<List<BundleSummary>> list({bool includeArchived = false}) async =>
      const [];
}

/// The browser's sound as the page sees it: it plays only once the browser
/// has had the teacher's interaction ([userActivated]) and the page unlocked
/// it.
class _FakeSound implements AlertSound {
  _FakeSound({required this.userActivated}) : _blocked = ValueNotifier(true);

  bool userActivated;
  final ValueNotifier<bool> _blocked;

  /// Every time the page asked for the sound.
  int plays = 0;

  /// The ones the browser let through.
  int audible = 0;

  @override
  ValueListenable<bool> get blocked => _blocked;

  @override
  void unlock() {
    if (userActivated) _blocked.value = false;
  }

  @override
  void play() {
    plays++;
    if (!_blocked.value) audible++;
  }

  @override
  void dispose() {}
}

typedef _Page = ({_StubHub hub, _FakeSound sound, LiveAlertSettings settings});

/// Every tab title the app asked for, by way of SystemChrome.
final List<String> _titles = [];

/// The tab title the app asked for last.
String? get _tabTitle => _titles.isEmpty ? null : _titles.last;

/// Whether the app ever put a count in the tab title.
bool get _countShown => _titles.any((t) => t.startsWith('('));

Future<_Page> _pumpLiveSession(
  WidgetTester tester, {
  bool userActivated = true,
  DateTime Function()? clock,
}) async {
  tester.view.physicalSize = const Size(1400, 1000);
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.resetPhysicalSize);
  addTearDown(tester.view.resetDevicePixelRatio);
  _titles.clear();
  tester.binding.defaultBinaryMessenger.setMockMethodCallHandler(
    SystemChannels.platform,
    (call) async {
      if (call.method == 'SystemChrome.setApplicationSwitcherDescription') {
        _titles.add((call.arguments as Map)['label'] as String);
      }
      return null;
    },
  );
  addTearDown(
    () => tester.binding.defaultBinaryMessenger.setMockMethodCallHandler(
      SystemChannels.platform,
      null,
    ),
  );

  final hub = _StubHub();
  final sound = _FakeSound(userActivated: userActivated);
  final settings = LiveAlertSettings(store: MemoryAlertSettingsStore());
  await tester.pumpWidget(
    MaterialApp(
      theme: ThemeData(splashFactory: NoSplash.splashFactory),
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      home: SessionPage(
        sessionId: _sessionId,
        tokens: AuthTokenStore(),
        sessions: _FakeSessions(),
        bundles: _FakeBundles(),
        apiBaseUrl: Uri.parse('http://localhost'),
        hubClientFactory: ({required apiBaseUrl, required tokenProvider}) =>
            hub,
        alertServices: LiveAlertServices(
          settings: settings,
          sound: sound,
          clock: clock ?? DateTime.now,
        ),
      ),
    ),
  );
  await tester.pumpAndSettle();
  return (hub: hub, sound: sound, settings: settings);
}

/// The teacher switches to another tab: the browser reports the page hidden.
void _hidePage(WidgetTester tester) {
  tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.inactive);
  tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.hidden);
  addTearDown(() => _showPage(tester));
}

void _showPage(WidgetTester tester) {
  if (tester.binding.lifecycleState != AppLifecycleState.hidden) return;
  tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.inactive);
  tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.resumed);
}

Map<String, dynamic> _request(String user, String host) => {
  'sessionId': _sessionId,
  'userId': user,
  'userDisplayName': user,
  'host': host,
};

void main() {
  testWidgets(
    'in a background tab a request plays the sound and counts in the title '
    'with no frame drawn; coming back clears it',
    (tester) async {
      final page = await _pumpLiveSession(tester);
      expect(page.sound.blocked.value, isFalse, reason: 'opened by a click');

      _hidePage(tester);
      page.hub.emit('UnblockRequested', _request('ada', 'docs.example'));
      // A background tab draws no frames: idle runs what's queued, and
      // draws nothing.
      await tester.idle();

      expect(page.sound.plays, 1);
      expect(page.sound.audible, 1);
      expect(_tabTitle, '(1) Anchor');

      // The rest of the burst counts, without another sound.
      page.hub.emit('UnblockRequested', _request('bo', 'docs.example'));
      page.hub.emit('UnblockRequested', _request('cas', 'docs.example'));
      await tester.idle();
      expect(page.sound.plays, 1);
      expect(_tabTitle, '(3) Anchor');

      _showPage(tester);
      await tester.pumpAndSettle();
      expect(_tabTitle, 'Anchor');
    },
  );

  testWidgets('with the page in view a request plays the sound, and the '
      'title stays as it is', (tester) async {
    final page = await _pumpLiveSession(tester);

    page.hub.emit('UnblockRequested', _request('ada', 'docs.example'));
    await tester.pumpAndSettle();

    expect(page.sound.plays, 1);
    expect(_countShown, isFalse);
  });

  testWidgets('joins, other sessions and an ended session raise nothing', (
    tester,
  ) async {
    final page = await _pumpLiveSession(tester);
    _hidePage(tester);

    page.hub.emit('ParticipantStateChanged', {
      'sessionId': _sessionId,
      'userId': 'ada',
      'state': 'Joined',
    });
    page.hub.emit('AgentReconnected', {
      'sessionId': _sessionId,
      'userId': 'ada',
    });
    page.hub.emit(
      'UnblockRequested',
      _request('bo', 'docs.example')..['sessionId'] = 'another-session',
    );
    page.hub.emit('SessionEnded', {'sessionId': _sessionId});
    page.hub.emit('HeartbeatLost', {'sessionId': _sessionId, 'userId': 'ada'});
    await tester.idle();

    expect(page.sound.plays, 0);
    expect(_countShown, isFalse);
  });

  testWidgets(
    'a student who leaves plays the sound once, though HeartbeatLost follows',
    (tester) async {
      var now = DateTime(2026, 6, 12, 9, 30);
      final page = await _pumpLiveSession(tester, clock: () => now);
      _hidePage(tester);

      page.hub.emit('ParticipantStateChanged', {
        'sessionId': _sessionId,
        'userId': 'ada',
        'displayName': 'Ada',
        'state': 'Left',
      });
      await tester.idle();
      // The heartbeat timeout passes the burst window.
      now = now.add(const Duration(seconds: 20));
      page.hub.emit('HeartbeatLost', {
        'sessionId': _sessionId,
        'userId': 'ada',
      });
      await tester.idle();

      expect(page.sound.plays, 1);
      expect(_tabTitle, '(1) Anchor');

      // Someone else's agent going quiet is news.
      page.hub.emit('HeartbeatLost', {'sessionId': _sessionId, 'userId': 'bo'});
      await tester.idle();
      expect(page.sound.plays, 2);
      expect(_tabTitle, '(2) Anchor');
    },
  );

  testWidgets('after a reload the page says sound is off until the teacher '
      'clicks it, and the click turns it on', (tester) async {
    final page = await _pumpLiveSession(tester, userActivated: false);

    expect(find.text(_blockedLine), findsOneWidget);
    // An alert in the meantime is asked for, and the browser holds it back.
    page.hub.emit('TamperDetected', {
      'sessionId': _sessionId,
      'userId': 'ada',
      'userDisplayName': 'Ada',
      'kind': 'inprivate_opened',
    });
    await tester.pumpAndSettle();
    expect(page.sound.plays, 1);
    expect(page.sound.audible, 0);

    // The teacher clicks somewhere on the page; the browser now allows it.
    page.sound.userActivated = true;
    await tester.tap(find.text('Activity'));
    await tester.pumpAndSettle();

    expect(find.text(_blockedLine), findsNothing);
    expect(page.sound.blocked.value, isFalse);
  });

  testWidgets('the line stays away while the teacher has sound off', (
    tester,
  ) async {
    final page = await _pumpLiveSession(tester, userActivated: false);
    expect(find.text(_blockedLine), findsOneWidget);

    page.settings.sound = false;
    await tester.pumpAndSettle();
    expect(find.text(_blockedLine), findsNothing);
  });

  testWidgets('the bell in the header opens the Notifications dialog, whose '
      'switches take effect at once', (tester) async {
    final page = await _pumpLiveSession(tester);

    await tester.tap(find.byTooltip('Notifications'));
    await tester.pumpAndSettle();
    expect(find.text('Notifications'), findsWidgets);
    expect(find.text('Play a sound'), findsOneWidget);
    for (final label in [
      'A student asks for access to a site',
      'A student leaves or declines the session',
      "A student's agent stops reporting",
      'Tampering is detected',
    ]) {
      expect(find.text(label), findsOneWidget, reason: label);
    }

    // Test plays the sound.
    await tester.tap(find.text('Test sound'));
    await tester.pumpAndSettle();
    expect(page.sound.plays, 1);

    // No more access-request alerts, and no sound for the rest.
    await tester.tap(find.text('A student asks for access to a site'));
    await tester.tap(find.text('Play a sound'));
    await tester.pumpAndSettle();
    expect(page.settings.isOn(LiveAlertCategory.accessRequest), isFalse);
    expect(page.settings.sound, isFalse);
    await tester.tap(find.text('Close'));
    await tester.pumpAndSettle();

    _hidePage(tester);
    page.hub.emit('UnblockRequested', _request('ada', 'docs.example'));
    page.hub.emit('TamperDetected', {
      'sessionId': _sessionId,
      'userId': 'bo',
      'userDisplayName': 'Bo',
      'kind': 'inprivate_opened',
    });
    await tester.idle();
    expect(page.sound.plays, 1, reason: 'only the Test');
    expect(_tabTitle, '(1) Anchor', reason: 'the tamper report counts');
  });
}
