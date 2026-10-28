import 'dart:async';

import 'package:anchor_dashboard/alerts/alert_platform.dart';
import 'package:anchor_dashboard/alerts/alert_sound.dart';
import 'package:anchor_dashboard/alerts/live_alert_services.dart';
import 'package:anchor_dashboard/alerts/live_alert_settings.dart';
import 'package:anchor_dashboard/alerts/live_alerts.dart';
import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/auth_token_store.dart';
import 'package:anchor_dashboard/api/bundles_api.dart';
import 'package:anchor_dashboard/api/classes_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/auth/msal_auth_service.dart';
import 'package:anchor_dashboard/main.dart';
import 'package:anchor_dashboard/realtime/session_hub_client.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';

import 'support/browser_tab.dart';
import 'support/e2e_binding.dart';

// Real-app e2e for live-session alerts (#454). While a teacher presents or
// works in another tab, the live page plays a short sound and counts the
// alerts they haven't seen in the tab's title, "(3) Anchor". This boots the
// real AnchorDashboard (real router, navigation, layout and fonts under
// `flutter drive`) against fake APIs and a stubbed live feed, as
// live_session_test.dart does, and checks:
//   - in a background tab an access request plays the sound and counts in the
//     title, with no frame drawn (a hidden tab draws none); coming back
//     clears the count; twenty requests within seconds play the sound once,
//   - leaving, declining, a lost agent and tampering alert the same way;
//     joining doesn't; a Leave session followed by HeartbeatLost is one,
//   - sound and each category switch off from the Notifications dialog,
//     opened from the app bar before a lesson or from the live page; the
//     choices survive a reload (the browser's localStorage in a browser), and
//     Test plays the sound,
//   - after a reload of the live page, while the browser still holds sound
//     back, the page says so and the first click turns it on,
//   - the browser's own sound (Web Audio) starts on the real page, and its
//     chime is audible.
//
// A browser driven by a test never lets a page play sound (its clicks aren't
// the user's), so these inject a sound that records what the page asks of it
// and holds sound back the way the browser does until it has had the
// teacher's interaction. The tab is hidden the way the browser reports it:
// through the app lifecycle, which the web engine drives from
// `visibilitychange`.

const _sessionId = '11111111-2222-3333-4444-555555555555';
const _blockedLine = 'Click anywhere to turn on sound.';
final _startedAt = DateTime(2026, 6, 12, 9, 15);

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

/// Stands in for the SignalR client; the test pushes the hub's broadcasts.
class _StubHub extends SessionHubClient {
  _StubHub()
    : super(apiBaseUrl: Uri.parse('http://localhost'), tokenProvider: _noToken);

  static Future<String?> _noToken() async => null;

  final _ctrl = StreamController<SessionEvent>.broadcast();

  @override
  Stream<SessionEvent> get events => _ctrl.stream;

  void emit(String kind, Map<String, dynamic> payload) => _ctrl.add(
    SessionEvent(
      kind: kind,
      payload: {'sessionId': _sessionId, ...payload},
      at: DateTime.now(),
    ),
  );

  void request(String student, String host) => emit('UnblockRequested', {
    'userId': student,
    'userDisplayName': student,
    'host': host,
    'url': 'https://$host/',
  });

  void state(String student, String state) => emit('ParticipantStateChanged', {
    'userId': student,
    'displayName': student,
    'state': state,
  });

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

  List<UnblockRequestSummary> pending = const [];

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
  Future<SessionEndOptions> endOptions(String classId) async =>
      SessionEndOptions();

  @override
  Future<StartSessionResponse> startSession(
    String classId, {
    List<String> bundleIds = const <String>[],
    SessionEnd? end,
  }) async => StartSessionResponse(
    id: _sessionId,
    classId: classId,
    joinCode: 'ABC123',
    startedAt: _startedAt,
  );

  @override
  Future<SessionDetail> getSession(String sessionId) async => SessionDetail(
    id: sessionId,
    classId: 'c1',
    className: 'Math 101',
    joinCode: 'ABC123',
    startedAt: _startedAt,
    endedAt: null,
    summaries: const [],
    recentEvents: const [],
    participants: [
      for (final name in ['Ada', 'Bo', 'Cas', 'Dina'])
        _participant(name, ParticipantLiveState.joined),
    ],
    bundles: const [],
    grants: const [],
  );

  @override
  Future<List<UnblockRequestSummary>> unblockRequests(String sessionId) async =>
      pending;
}

class _FakeBundles extends BundlesApi {
  _FakeBundles() : super(_dummyClient());

  @override
  Future<List<BundleSummary>> list({bool includeArchived = false}) async =>
      const [];
}

/// The page's sound, as the browser treats it: held back until the browser
/// has had the teacher's interaction ([userActivated]) and the page unlocked
/// it.
class _WatchedSound implements AlertSound {
  _WatchedSound({required this.userActivated});

  /// Whether the browser has had the teacher's click. Clicking through the
  /// dashboard to the live page counts; a reload of the live page doesn't.
  bool userActivated;
  final _blocked = ValueNotifier<bool>(true);

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

class _Clock {
  DateTime now = DateTime(2026, 6, 12, 9, 30);

  /// Past the ten-second burst window.
  void afterTheBurst() => now = now.add(const Duration(seconds: 11));
}

typedef _Harness = ({
  _StubHub hub,
  _FakeSessions sessions,
  _WatchedSound sound,
  _Clock clock,
});

/// A fresh settings store: the browser's localStorage in a browser (cleared
/// here, since it outlives a test), memory in a VM copy.
AlertSettingsStore _freshStore() => createAlertSettingsStore()..write('{}');

/// Boots the real app signed in. Unless [at] gives a page to load (a reload),
/// it drives the real Home → Start session navigation to the live page, so
/// the teacher's click has let the browser play sound.
Future<_Harness> _boot(
  WidgetTester tester, {
  required AlertSettingsStore store,
  String? at,
  Future<void> Function()? onHome,
}) async {
  followTabTitle(tester);
  if (at != null) _loadAt(tester, at);
  final hub = _StubHub();
  final sessions = _FakeSessions();
  final sound = _WatchedSound(userActivated: at == null);
  final clock = _Clock();
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

  // A new app each time, as after a reload: nothing carries over but the
  // store.
  await tester.pumpWidget(const SizedBox.shrink());
  await tester.pumpWidget(
    AnchorDashboard(
      tokens: tokens,
      auth: _FakeAuth(),
      api: _dummyClient(),
      sessions: sessions,
      bundles: _FakeBundles(),
      classes: ClassesApi(_dummyClient()),
      apiBaseUrl: Uri.parse('http://localhost'),
      hubClientFactory: ({required apiBaseUrl, required tokenProvider}) => hub,
      alertServices: LiveAlertServices(
        settings: LiveAlertSettings(store: store),
        sound: sound,
        clock: () => clock.now,
      ),
    ),
  );
  await tester.pumpAndSettle();

  if (at == null) {
    await onHome?.call();
    await tester.tap(find.textContaining('Start session'));
    await tester.pumpAndSettle();
  }
  expect(find.text('Math 101'), findsOneWidget, reason: 'on the live page');
  expect(find.text('LIVE'), findsOneWidget);
  return (hub: hub, sessions: sessions, sound: sound, clock: clock);
}

/// Loads the app "at" [location], as the browser does on a reload; see
/// deep_link_test.dart.
void _loadAt(WidgetTester tester, String location) {
  final binding = tester.binding;
  final messenger = binding.defaultBinaryMessenger;
  final channel = SystemChannels.navigation.name;

  binding.platformDispatcher.defaultRouteNameTestValue = location;
  addTearDown(binding.platformDispatcher.clearDefaultRouteNameTestValue);

  messenger.setMockMessageHandler(channel, (ByteData? message) {
    binding.platformDispatcher.clearDefaultRouteNameTestValue();
    return messenger.delegate.send(channel, message);
  });
  addTearDown(() => messenger.setMockMessageHandler(channel, null));
}

/// The teacher switches to another tab, or a slide show covers the browser:
/// it reports the page hidden. A hidden page draws no frames, so until
/// [_showPage] the test only lets what's queued run ([WidgetTester.idle]).
void _hidePage(WidgetTester tester) {
  tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.inactive);
  tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.hidden);
  addTearDown(() => _showPage(tester));
}

/// The teacher comes back to the tab.
void _showPage(WidgetTester tester) {
  if (tester.binding.lifecycleState != AppLifecycleState.hidden) return;
  tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.inactive);
  tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.resumed);
}

Future<void> _setSwitch(WidgetTester tester, String label, bool on) async {
  final tile = find.widgetWithText(SwitchListTile, label);
  expect(tile, findsOneWidget, reason: label);
  if (tester.widget<SwitchListTile>(tile).value != on) {
    await tester.tap(tile);
    await tester.pumpAndSettle();
  }
  expect(tester.widget<SwitchListTile>(tile).value, on, reason: label);
}

bool _switchOn(WidgetTester tester, String label) => tester
    .widget<SwitchListTile>(find.widgetWithText(SwitchListTile, label))
    .value;

void main() {
  ensureE2eBinding();

  testWidgets(
    'in a background tab an access request plays the sound and counts in the '
    'title; twenty of them play it once; coming back clears the count',
    (tester) async {
      final h = await _boot(tester, store: _freshStore());
      expect(tabTitle(), 'Anchor');
      expect(find.text(_blockedLine), findsNothing, reason: 'clicked through');

      _hidePage(tester);
      final now = DateTime(2026, 6, 12, 9, 31);
      h.sessions.pending = [
        UnblockRequestSummary(
          host: 'docs.example.com',
          count: 20,
          firstRequestedAt: now,
          latestRequestedAt: now,
          requesters: [
            for (var i = 0; i < 20; i++)
              UnblockRequestRequester(
                userId: 'student$i',
                displayName: 'Student $i',
                requestedAt: now,
              ),
          ],
        ),
      ];
      h.hub.request('student0', 'docs.example.com');
      await tester.idle();

      // At once, with no frame drawn.
      expect(h.sound.plays, 1);
      expect(h.sound.audible, 1);
      expect(tabTitle(), '(1) Anchor');

      // The rest of the class asks for the same site within a few seconds.
      for (var i = 1; i < 20; i++) {
        h.clock.now = h.clock.now.add(const Duration(milliseconds: 250));
        h.hub.request('student$i', 'docs.example.com');
      }
      await tester.idle();
      expect(h.sound.plays, 1, reason: 'one burst, one sound');
      expect(tabTitle(), '(20) Anchor');

      // The teacher comes back: the count clears, and the request is there.
      _showPage(tester);
      await tester.pumpAndSettle();
      expect(tabTitle(), 'Anchor');
      expect(find.text('docs.example.com'), findsOneWidget);
      expect(h.sound.plays, 1);
      expect(tester.takeException(), isNull);
    },
  );

  testWidgets(
    'leaving, declining, a lost agent and tampering alert; joining does not; '
    'a Leave session followed by HeartbeatLost alerts once',
    (tester) async {
      final h = await _boot(tester, store: _freshStore());
      _hidePage(tester);

      // Joining never alerts.
      h.hub.state('Eli', 'Joined');
      h.hub.emit('AgentReconnected', {'userId': 'Eli'});
      await tester.idle();
      expect(h.sound.plays, 0);
      expect(tabTitle(), 'Anchor');

      // Ada leaves with Leave session...
      h.hub.state('Ada', 'Left');
      await tester.idle();
      expect(h.sound.plays, 1);
      expect(tabTitle(), '(1) Anchor');
      // ...and twenty seconds later her heartbeat is reported lost (#448).
      h.clock.now = h.clock.now.add(const Duration(seconds: 20));
      h.hub.emit('HeartbeatLost', {'userId': 'Ada'});
      await tester.idle();
      expect(h.sound.plays, 1, reason: 'one leave, one alert');
      expect(tabTitle(), '(1) Anchor');

      h.clock.afterTheBurst();
      h.hub.state('Bo', 'Declined');
      await tester.idle();
      expect(h.sound.plays, 2);
      expect(tabTitle(), '(2) Anchor');

      h.clock.afterTheBurst();
      h.hub.emit('HeartbeatLost', {'userId': 'Cas'});
      await tester.idle();
      expect(h.sound.plays, 3);
      expect(tabTitle(), '(3) Anchor');

      h.clock.afterTheBurst();
      h.hub.emit('TamperDetected', {
        'userId': 'Dina',
        'userDisplayName': 'Dina',
        'kind': 'inprivate_opened',
      });
      await tester.idle();
      expect(h.sound.plays, 4);
      expect(h.sound.audible, 4);
      expect(tabTitle(), '(4) Anchor');

      _showPage(tester);
      await tester.pumpAndSettle();
      expect(tabTitle(), 'Anchor');
      expect(tester.takeException(), isNull);
    },
  );

  testWidgets(
    'sound and each kind of alert switch off from the Notifications dialog, '
    'in the app bar or on the live page; the choices survive a reload, and '
    'Test plays the sound',
    (tester) async {
      final store = _freshStore();
      final h = await _boot(
        tester,
        store: store,
        onHome: () async {
          // Before the lesson, from the app bar.
          await tester.tap(find.byKey(const Key('notifications')));
          await tester.pumpAndSettle();
          expect(find.text('Notifications'), findsWidgets);
          await _setSwitch(
            tester,
            'A student asks for access to a site',
            false,
          );
          await tester.tap(find.byKey(const Key('alerts-test')));
          await tester.pumpAndSettle();
          await tester.tap(find.text('Close'));
          await tester.pumpAndSettle();
        },
      );
      expect(h.sound.plays, 1, reason: 'the Test button');

      _hidePage(tester);
      h.hub.request('Ada', 'docs.example.com');
      await tester.idle();
      expect(h.sound.plays, 1, reason: 'access requests are off');
      expect(tabTitle(), 'Anchor');
      h.hub.emit('TamperDetected', {
        'userId': 'Bo',
        'userDisplayName': 'Bo',
        'kind': 'inprivate_opened',
      });
      await tester.idle();
      expect(h.sound.plays, 2);
      expect(tabTitle(), '(1) Anchor');
      _showPage(tester);
      await tester.pumpAndSettle();

      // During the lesson, from the live page: sound off.
      await tester.tap(find.byKey(const Key('session-notifications')));
      await tester.pumpAndSettle();
      expect(_switchOn(tester, 'A student asks for access to a site'), isFalse);
      await _setSwitch(tester, 'Play a sound', false);
      await tester.tap(find.text('Close'));
      await tester.pumpAndSettle();

      h.clock.afterTheBurst();
      _hidePage(tester);
      h.hub.state('Cas', 'Left');
      await tester.idle();
      expect(h.sound.plays, 2, reason: 'sound is off');
      expect(tabTitle(), '(1) Anchor', reason: 'it still counts');
      _showPage(tester);
      await tester.pumpAndSettle();

      // A reload: a new app, reading the same browser storage.
      final reloaded = await _boot(tester, store: store);
      await tester.tap(find.byKey(const Key('session-notifications')));
      await tester.pumpAndSettle();
      expect(_switchOn(tester, 'Play a sound'), isFalse);
      expect(_switchOn(tester, 'A student asks for access to a site'), isFalse);
      expect(_switchOn(tester, 'Tampering is detected'), isTrue);
      // Test plays the sound whatever the switches say.
      await tester.tap(find.byKey(const Key('alerts-test')));
      await tester.pumpAndSettle();
      expect(reloaded.sound.plays, 1);
      expect(reloaded.sound.audible, 1);
      expect(tester.takeException(), isNull);
    },
  );

  testWidgets(
    'after a reload of the live page the page says sound is off until the '
    'teacher clicks, and the click turns it on',
    (tester) async {
      final h = await _boot(
        tester,
        store: _freshStore(),
        at: '/session/$_sessionId',
      );

      expect(find.text(_blockedLine), findsOneWidget);

      // An alert meanwhile: the page asks, and the browser holds it back.
      h.hub.request('Ada', 'docs.example.com');
      await tester.pumpAndSettle();
      expect(h.sound.plays, 1);
      expect(h.sound.audible, 0);
      expect(find.text(_blockedLine), findsOneWidget);

      // The teacher clicks somewhere on the page, which lets the browser
      // play sound, and the page takes it up.
      h.sound.userActivated = true;
      await tester.tap(find.text('Activity'));
      await tester.pumpAndSettle();
      expect(find.text(_blockedLine), findsNothing);

      h.clock.afterTheBurst();
      h.hub.state('Bo', 'Left');
      await tester.pumpAndSettle();
      expect(h.sound.plays, 2);
      expect(h.sound.audible, 1);
      expect(tester.takeException(), isNull);
    },
  );

  testWidgets(
    "the browser's own sound starts on the live page, and its chime is "
    'audible',
    (tester) async {
      // The real Web Audio sound and localStorage, as production builds them,
      // with the settings left as a new teacher finds them.
      _freshStore();
      final services = LiveAlertServices.browser();
      expect(services.settings.sound, isTrue);
      expect(services.settings.isOn(LiveAlertCategory.accessRequest), isTrue);
      followTabTitle(tester);
      final hub = _StubHub();
      await tester.pumpWidget(
        AnchorDashboard(
          tokens: AuthTokenStore()
            ..setSession(
              token: 'fake-token',
              account: const AccountInfo(
                homeAccountId: 'home-1',
                username: 'teacher@school.example',
                displayName: 'Teacher',
                department: null,
              ),
            ),
          auth: _FakeAuth(),
          api: _dummyClient(),
          sessions: _FakeSessions(),
          bundles: _FakeBundles(),
          classes: ClassesApi(_dummyClient()),
          apiBaseUrl: Uri.parse('http://localhost'),
          hubClientFactory: ({required apiBaseUrl, required tokenProvider}) =>
              hub,
          alertServices: services,
        ),
      );
      await tester.pumpAndSettle();
      await tester.tap(find.textContaining('Start session'));
      await tester.pumpAndSettle();

      // A test's taps aren't the user's, so a browser holds sound back here,
      // and the page says so; a VM copy has no sound to hold back.
      expect(
        find.text(_blockedLine),
        services.sound.blocked.value ? findsOneWidget : findsNothing,
      );
      _hidePage(tester);
      hub.request('Ada', 'docs.example.com');
      await tester.idle();
      expect(tabTitle(), '(1) Anchor');
      _showPage(tester);
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('session-notifications')));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('alerts-test')));
      await tester.pumpAndSettle();
      expect(tester.takeException(), isNull);

      // The chime itself, rendered by the browser's Web Audio.
      final chime = await renderAlertChime();
      if (chime != null) {
        expect(chime.peak, greaterThan(0.1), reason: 'audible');
        expect(chime.tail, lessThan(0.001), reason: 'short: done in 0.7 s');
      }

      await tester.pumpWidget(const SizedBox.shrink());
      services.sound.dispose();
    },
  );
}
