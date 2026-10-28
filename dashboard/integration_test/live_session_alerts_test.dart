import 'dart:async';

import 'package:anchor_dashboard/alerts/alert_platform.dart';
import 'package:anchor_dashboard/alerts/alert_popups.dart';
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
//     chime is audible,
//   - pop-ups (#455): nothing asks for the browser's permission until the
//     teacher turns pop-ups on; with it allowed and the page in a background
//     tab, an access request pops up at once, naming no one until the teacher
//     turns names on; twenty within seconds pop up once, and a later one
//     replaces it; a click brings the live page to the front at the pending
//     requests; the Test button shows a sample; blocked, the dialog says so
//     with the steps to allow them, and shows them allowed once the teacher
//     allows them in the browser and comes back to the tab, without a
//     reload; a browser without pop-ups says so,
//   - the dialog shows the permission the real browser holds.
//
// A browser driven by a test never lets a page play sound (its clicks aren't
// the user's), so these inject a sound that records what the page asks of it
// and holds sound back the way the browser does until it has had the
// teacher's interaction. The tab is hidden the way the browser reports it:
// through the app lifecycle, which the web engine drives from
// `visibilitychange`.
//
// Nor can a test answer the browser's permission prompt, or see a system
// pop-up, so the pop-up tests inject pop-ups that stand in for the
// Notification API the way the browser treats it: the permission it holds,
// what the teacher answers when it asks, and the pop-ups it shows.

const _sessionId = '11111111-2222-3333-4444-555555555555';
const _blockedLine = 'Click anywhere to turn on sound.';
const _popupsSwitch = 'Show pop-ups';
const _namesSwitch = 'Show student names and sites in pop-ups';
const _notAsked = 'Your browser asks for permission when you turn them on.';
const _allowed = 'Allowed in this browser.';
const _blocked = 'Blocked in this browser.';
const _unsupported = "This browser can't show pop-ups.";
const _howTo = 'To allow pop-ups in Edge or Chrome:';
const _steps = [
  '1. Click the icon left of the address bar.',
  '2. Set Notifications to Allow.',
  '3. Reload the page.',
];
const _windows =
    'Still no pop-ups? Check that notifications for your browser are on in '
    'Windows: Settings → System → Notifications.';
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

/// The browser's pop-ups (#455), as the browser treats them: a permission it
/// holds for the dashboard ([browser]: `default`, `granted` or `denied`, and
/// null for a browser without them), which the teacher answers ([answer]) when
/// it asks, and can change in its site settings at any time; and the pop-ups
/// it shows, only while they're allowed.
class _BrowserPopups implements AlertPopups {
  _BrowserPopups({this.browser = 'default', this.answer = 'granted'})
    : _permission = ValueNotifier(_of(browser));

  String? browser;
  String answer;

  /// How often the page asked the browser for permission.
  int asked = 0;

  /// How often a click on a pop-up brought the dashboard to the front.
  int broughtToFront = 0;

  /// What the browser does then: it shows the page.
  VoidCallback? onFront;

  /// Every pop-up the browser showed, in order.
  final List<AlertPopup> shown = [];

  /// The pop-ups on screen, by tag: a new one replaces one with its tag.
  final Map<String, AlertPopup> onScreen = {};

  final ValueNotifier<PopupPermission> _permission;

  static PopupPermission _of(String? browser) => switch (browser) {
    null => PopupPermission.unsupported,
    'granted' => PopupPermission.allowed,
    'denied' => PopupPermission.blocked,
    _ => PopupPermission.notAsked,
  };

  @override
  ValueListenable<PopupPermission> get permission => _permission;

  @override
  void refresh() => _permission.value = _of(browser);

  @override
  Future<PopupPermission> request() async {
    asked++;
    if (browser == 'default') browser = answer;
    refresh();
    return _permission.value;
  }

  @override
  void show(AlertPopup popup) {
    if (browser != 'granted') return;
    shown.add(popup);
    onScreen[popup.tag] = popup;
  }

  @override
  void close(String tag) => onScreen.remove(tag);

  @override
  void bringPageToFront() {
    broughtToFront++;
    onFront?.call();
  }

  /// The teacher clicks the pop-up with [tag].
  void click(String tag) => onScreen[tag]!.onClick?.call();

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
  _BrowserPopups popups,
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
  _BrowserPopups? popups,
}) async {
  followTabTitle(tester);
  if (at != null) _loadAt(tester, at);
  final hub = _StubHub();
  final sessions = _FakeSessions();
  final sound = _WatchedSound(userActivated: at == null);
  final clock = _Clock();
  // Brought to the front, the browser shows the page.
  final browserPopups = (popups ?? _BrowserPopups())
    ..onFront = () => _showPage(tester);
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
        popups: browserPopups,
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
  return (
    hub: hub,
    sessions: sessions,
    sound: sound,
    clock: clock,
    popups: browserPopups,
  );
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

/// The teacher leaves the page for the browser's own UI (its site settings,
/// left of the address bar) and comes back to it: the window loses the focus
/// and gets it back.
void _leaveAndComeBack(WidgetTester tester) {
  tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.inactive);
  tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.resumed);
}

/// [students] asking for docs.example.com, as the backend lists them.
UnblockRequestSummary _pendingFor(int students) {
  final at = DateTime(2026, 6, 12, 9, 31);
  return UnblockRequestSummary(
    host: 'docs.example.com',
    count: students,
    firstRequestedAt: at,
    latestRequestedAt: at,
    requesters: [
      for (var i = 0; i < students; i++)
        UnblockRequestRequester(
          userId: 'student$i',
          displayName: 'Student $i',
          requestedAt: at,
        ),
    ],
  );
}

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

  testWidgets(
    'pop-ups: nothing asks for permission until the teacher turns them on; '
    'then a request in a background tab pops up, naming no one; twenty pop '
    'up once, and a later one replaces it; a click brings the live page to '
    'the front at the pending requests',
    (tester) async {
      // A short window, so the pending requests can scroll out of view.
      tester.view.physicalSize = const Size(1400, 600);
      final h = await _boot(tester, store: _freshStore());
      final popups = h.popups;
      h.sessions.pending = [_pendingFor(1)];

      // Neither opening the live page nor an alert asks.
      _hidePage(tester);
      h.hub.request('student0', 'docs.example.com');
      await tester.idle();
      expect(h.sound.plays, 1);
      expect(popups.asked, 0);
      expect(popups.shown, isEmpty);
      _showPage(tester);
      await tester.pumpAndSettle();

      // From the live page. The first time the teacher closes the browser's
      // prompt; the second time they allow pop-ups.
      await tester.tap(find.byKey(const Key('session-notifications')));
      await tester.pumpAndSettle();
      expect(popups.asked, 0, reason: 'opening the dialog asks nothing');
      expect(_switchOn(tester, _popupsSwitch), isFalse);
      expect(find.text(_notAsked), findsOneWidget);
      popups.answer = 'default';
      await tester.tap(find.text(_popupsSwitch));
      await tester.pumpAndSettle();
      expect(popups.asked, 1);
      expect(_switchOn(tester, _popupsSwitch), isFalse);
      expect(find.text(_notAsked), findsOneWidget);
      popups.answer = 'granted';
      await tester.tap(find.text(_popupsSwitch));
      await tester.pumpAndSettle();
      expect(popups.asked, 2);
      expect(_switchOn(tester, _popupsSwitch), isTrue);
      expect(find.text(_allowed), findsOneWidget);
      expect(_switchOn(tester, _namesSwitch), isFalse, reason: 'no names');
      await tester.tap(find.text('Close'));
      await tester.pumpAndSettle();

      // With the page in view, the teacher sees a request on it: no pop-up.
      h.clock.afterTheBurst();
      h.hub.request('student99', 'docs.example.com');
      await tester.pumpAndSettle();
      expect(h.sound.plays, 2);
      expect(popups.shown, isEmpty, reason: 'the page is in view');

      // The teacher scrolled down to the feed.
      final ScrollPosition page = Scrollable.of(
        tester.element(find.text('Pending requests')),
      ).position;
      page.jumpTo(page.maxScrollExtent);
      await tester.pumpAndSettle();
      expect(
        find.text('Pending requests').hitTestable(),
        findsNothing,
        reason: 'scrolled out of view',
      );

      // In a background tab, the class asks for the same site within a few
      // seconds.
      h.clock.afterTheBurst();
      _hidePage(tester);
      h.sessions.pending = [_pendingFor(21)];
      for (var i = 1; i <= 20; i++) {
        h.hub.request('student$i', 'docs.example.com');
        h.clock.now = h.clock.now.add(const Duration(milliseconds: 250));
      }
      await tester.idle();
      expect(popups.shown, hasLength(1), reason: 'one pop-up for the burst');
      final first = popups.shown.single;
      expect(first.title, 'New access request');
      expect(first.body, isEmpty);
      expect(tabTitle(), '(20) Anchor');

      // A while later, one more: it replaces the pop-up, with the count.
      h.clock.afterTheBurst();
      h.hub.request('student21', 'docs.example.com');
      await tester.idle();
      expect(popups.shown, hasLength(2));
      expect(popups.shown.last.title, 'New access requests (21)');
      expect(popups.onScreen, hasLength(1), reason: 'replaced, not stacked');
      for (final popup in popups.shown) {
        final String text = '${popup.title} ${popup.body}';
        expect(text, isNot(contains('docs.example.com')));
        expect(text, isNot(contains('student')));
      }

      // The teacher clicks it: the live page comes to the front, at the
      // pending requests.
      popups.click(popups.onScreen.keys.single);
      // Before a frame: a page left in the background draws none.
      expect(popups.broughtToFront, 1, reason: 'the page comes forward');
      expect(tester.binding.lifecycleState, AppLifecycleState.resumed);
      await tester.pumpAndSettle();
      expect(popups.onScreen, isEmpty, reason: 'clicked away');
      expect(tabTitle(), 'Anchor');
      expect(find.text('Pending requests').hitTestable(), findsOneWidget);
      expect(find.text('docs.example.com').hitTestable(), findsOneWidget);
      expect(tester.takeException(), isNull);
    },
  );

  testWidgets(
    'with names on a pop-up names the student and the site; Test shows a '
    'sample pop-up; the choices survive a reload',
    (tester) async {
      final store = _freshStore();
      final h = await _boot(
        tester,
        store: store,
        // The browser allows them already: there is nothing to ask.
        popups: _BrowserPopups(browser: 'granted'),
        onHome: () async {
          // Before the lesson, from the app bar.
          await tester.tap(find.byKey(const Key('notifications')));
          await tester.pumpAndSettle();
          await _setSwitch(tester, _popupsSwitch, true);
          await _setSwitch(tester, _namesSwitch, true);
          await tester.tap(find.byKey(const Key('alerts-test')));
          await tester.pumpAndSettle();
          await tester.tap(find.text('Close'));
          await tester.pumpAndSettle();
        },
      );
      final popups = h.popups;
      expect(popups.asked, 0);
      expect(h.sound.plays, 1, reason: 'the Test button');
      expect(popups.shown.single.title, 'Test pop-up');
      popups.click(popups.onScreen.keys.single);
      expect(popups.onScreen, isEmpty);

      _hidePage(tester);
      h.hub.request('Jana', 'docs.google.com');
      h.hub.state('Bo', 'Left');
      await tester.idle();
      expect(popups.shown.skip(1).map((p) => (p.title, p.body)), [
        ('New access request', 'Jana asks for access to docs.google.com'),
        ('A student left the session', 'Bo left the session'),
      ]);
      _showPage(tester);
      await tester.pumpAndSettle();
      expect(popups.onScreen, isEmpty, reason: 'seen on the page');

      // A reload: a new app, reading the same browser storage.
      final reloaded = await _boot(
        tester,
        store: store,
        popups: _BrowserPopups(browser: 'granted'),
      );
      await tester.tap(find.byKey(const Key('session-notifications')));
      await tester.pumpAndSettle();
      expect(_switchOn(tester, _popupsSwitch), isTrue);
      expect(_switchOn(tester, _namesSwitch), isTrue);
      expect(reloaded.popups.asked, 0);

      // Switched off, the browser still allows them, and none shows.
      await _setSwitch(tester, _popupsSwitch, false);
      await tester.tap(find.text('Close'));
      await tester.pumpAndSettle();
      _hidePage(tester);
      reloaded.hub.request('Jana', 'docs.google.com');
      await tester.idle();
      expect(reloaded.sound.plays, 1);
      expect(reloaded.popups.shown, isEmpty, reason: 'pop-ups are off');
      expect(tester.takeException(), isNull);
    },
  );

  testWidgets(
    'blocked: the dialog says so and shows the steps; once the teacher allows '
    'pop-ups in the browser and comes back to the tab, it shows them '
    'allowed, without a reload',
    (tester) async {
      // The teacher clicks Block when the browser asks.
      final h = await _boot(
        tester,
        store: _freshStore(),
        popups: _BrowserPopups(answer: 'denied'),
      );
      final popups = h.popups;
      await tester.tap(find.byKey(const Key('session-notifications')));
      await tester.pumpAndSettle();
      await tester.tap(find.text(_popupsSwitch));
      await tester.pumpAndSettle();
      expect(popups.asked, 1);
      expect(find.text(_blocked), findsOneWidget);
      expect(find.text(_howTo), findsOneWidget);
      for (final step in _steps) {
        expect(find.text(step), findsOneWidget, reason: step);
      }
      expect(find.text(_windows), findsOneWidget);

      // Meanwhile, in a background tab: the sound plays, the browser shows
      // no pop-up.
      _hidePage(tester);
      h.hub.request('Ada', 'docs.example.com');
      await tester.idle();
      expect(h.sound.plays, 1);
      expect(popups.shown, isEmpty);
      _showPage(tester);
      await tester.pumpAndSettle();
      expect(find.text(_blocked), findsOneWidget);

      // In the browser's site settings: Notifications, Allow. Back on the
      // page, the dialog reads it again.
      popups.browser = 'granted';
      _leaveAndComeBack(tester);
      await tester.pumpAndSettle();
      expect(find.text(_allowed), findsOneWidget);
      expect(find.text(_howTo), findsNothing);
      expect(popups.asked, 1, reason: 'not asked again');
      await tester.tap(find.text('Close'));
      await tester.pumpAndSettle();

      // Pop-ups show from now on.
      h.clock.afterTheBurst();
      _hidePage(tester);
      h.hub.request('Bo', 'docs.example.com');
      await tester.idle();
      expect(popups.shown.single.title, 'New access request');
      expect(tester.takeException(), isNull);
    },
  );

  testWidgets(
    "a browser without pop-ups: the dialog says so, and there's nothing to "
    'turn on',
    (tester) async {
      final h = await _boot(
        tester,
        store: _freshStore(),
        popups: _BrowserPopups(browser: null),
      );
      await tester.tap(find.byKey(const Key('session-notifications')));
      await tester.pumpAndSettle();
      expect(find.text(_unsupported), findsOneWidget);
      await tester.tap(find.text(_popupsSwitch));
      await tester.pumpAndSettle();
      expect(_switchOn(tester, _popupsSwitch), isFalse);
      expect(h.popups.asked, 0);
      expect(tester.takeException(), isNull);
    },
  );

  testWidgets(
    "the dialog shows the permission the browser holds for pop-ups, and "
    'neither the live page nor the dialog asks for it',
    (tester) async {
      // The real Notification API, as production builds it.
      final String? before = notificationPermission();
      _freshStore();
      final services = LiveAlertServices.browser();
      followTabTitle(tester);
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
              _StubHub(),
          alertServices: services,
        ),
      );
      await tester.pumpAndSettle();
      await tester.tap(find.textContaining('Start session'));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('session-notifications')));
      await tester.pumpAndSettle();

      final String? held = notificationPermission();
      // Chrome has the Notification API; a VM copy has no browser.
      expect(held, kIsWeb ? isNotNull : isNull);
      expect(
        find.text(switch (held) {
          null => _unsupported,
          'granted' => _allowed,
          'denied' => _blocked,
          _ => _notAsked,
        }),
        findsOneWidget,
        reason: 'the browser holds ${held ?? 'no Notification API'}',
      );
      expect(_switchOn(tester, _popupsSwitch), isFalse, reason: 'off at first');
      expect(held, before, reason: 'nothing asked');

      // Showing one where the browser doesn't allow it is nothing, not an
      // error.
      if (held != 'granted') {
        services.popups
          ..show(const AlertPopup(tag: 'anchor:e2e', title: 'e2e'))
          ..close('anchor:e2e');
      }
      await tester.tap(find.text('Close'));
      await tester.pumpAndSettle();
      expect(tester.takeException(), isNull);

      await tester.pumpWidget(const SizedBox.shrink());
      services.sound.dispose();
      services.popups.dispose();
    },
  );
}
