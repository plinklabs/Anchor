import 'dart:async';
import 'dart:math' as math;

import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/auth_token_store.dart';
import 'package:anchor_dashboard/api/bundles_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/l10n/app_localizations.dart';
import 'package:anchor_dashboard/pages/session_page.dart';
import 'package:anchor_dashboard/realtime/session_hub_client.dart';
import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';

// #422: the live page was a plain column of the header, every panel, then the
// feed. On a short window the panels took more than the window had: the feed
// got no height, the last panel (the allowed bundles) was clipped, and
// nothing scrolled. The page now scrolls, and the join code sits in the
// header row instead of a band of its own, dropping under the title when the
// row is too narrow. The real-font, real-window run is in
// integration_test/live_session_test.dart.

const _sessionId = 'aaaaaaaa-0000-0000-0000-000000000422';
const _code = 'ABC123';

ApiClient _dummyClient() => ApiClient(
  baseUrl: Uri.parse('http://localhost'),
  tokenProvider: () async => null,
);

class _StubHub extends SessionHubClient {
  _StubHub()
    : super(apiBaseUrl: Uri.parse('http://localhost'), tokenProvider: _noToken);

  static Future<String?> _noToken() async => null;

  final _ctrl = StreamController<SessionEvent>.broadcast();

  @override
  Stream<SessionEvent> get events => _ctrl.stream;

  void emit(String kind, Map<String, dynamic> payload) =>
      _ctrl.add(SessionEvent(kind: kind, payload: payload, at: DateTime.now()));

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

  final List<List<String>> updateCalls = [];
  List<SessionBundleInfo> currentBundles = const [];

  @override
  Future<SessionDetail> getSession(String sessionId) async => SessionDetail(
    id: sessionId,
    classId: 'c1',
    className: '6EWI',
    joinCode: _code,
    startedAt: DateTime(2026, 5, 26, 9, 15),
    endedAt: null,
    summaries: const [],
    recentEvents: const [],
    participants: [
      for (final name in ['Ada', 'Bo', 'Cas', 'Dina', 'Eli', 'Fien'])
        SessionParticipantInfo(
          userId: name,
          displayName: name,
          joinedAt: null,
          declinedAt: null,
          leftAt: null,
          state: ParticipantLiveState.joined,
          tampered: false,
        ),
    ],
    bundles: currentBundles,
    grants: const [],
  );

  @override
  Future<List<UnblockRequestSummary>> unblockRequests(String sessionId) async {
    final at = DateTime(2026, 5, 26, 9, 20);
    return [
      UnblockRequestSummary(
        host: 'chat.example.com',
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
      ),
    ];
  }

  @override
  Future<List<SessionBundleInfo>> updateBundles(
    String sessionId,
    List<String> bundleIds,
  ) async {
    updateCalls.add(bundleIds);
    currentBundles = [
      for (final id in bundleIds) SessionBundleInfo(id: id, name: id),
    ];
    return currentBundles;
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

Future<({_StubHub hub, _FakeSessions sessions})> _pumpLiveSession(
  WidgetTester tester,
  Size window,
) async {
  tester.view.physicalSize = window;
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.resetPhysicalSize);
  addTearDown(tester.view.resetDevicePixelRatio);

  final hub = _StubHub();
  final sessions = _FakeSessions();
  await tester.pumpWidget(
    MaterialApp(
      // NoSplash avoids loading the ink_sparkle fragment shader, which fails
      // to decode under the test engine when a tap triggers a ripple.
      theme: ThemeData(splashFactory: NoSplash.splashFactory),
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

/// The part of the window the page scrolls in.
Rect _page(WidgetTester tester) =>
    tester.getRect(find.byType(CustomScrollView));

bool _inView(WidgetTester tester, Finder finder) {
  final rect = tester.getRect(finder);
  return rect.top >= _page(tester).top && rect.bottom <= _page(tester).bottom;
}

/// Turns the mouse wheel by [dy] over the page's left edge, outside the
/// roster's and the requests' own lists, so the page itself scrolls.
Future<void> _wheel(WidgetTester tester, double dy) async {
  final pointer = TestPointer(1, PointerDeviceKind.mouse)
    ..hover(_page(tester).bottomLeft + const Offset(12, -40));
  await tester.sendEventToBinding(pointer.scroll(Offset(0, dy)));
  await tester.pumpAndSettle();
}

void main() {
  testWidgets(
    'on a short window the page scrolls to every panel and the feed',
    (tester) async {
      final page = await _pumpLiveSession(tester, const Size(1000, 500));

      // The header, the requests and the roster already fill the window. They
      // don't overflow it.
      expect(tester.takeException(), isNull);
      expect(find.text('Pending requests'), findsOneWidget);
      final chip = find.widgetWithText(FilterChip, 'Math');
      expect(_inView(tester, chip), isFalse, reason: 'below the window');

      // Scrolled down to, the bundles toggle as ever.
      for (var i = 0; i < 30 && !_inView(tester, chip); i++) {
        await _wheel(tester, 80);
      }
      expect(_inView(tester, chip), isTrue);
      await tester.tap(chip);
      await tester.pumpAndSettle();
      expect(page.sessions.updateCalls, [
        ['b1'],
      ]);

      // At the end of the page the feed shows its events, with room for more.
      page.hub.emit('HeartbeatLost', {
        'sessionId': _sessionId,
        'userId': 'Ada',
      });
      await tester.pumpAndSettle();
      await _wheel(tester, 5000);
      expect(_inView(tester, find.text('HeartbeatLost')), isTrue);
      expect(
        _page(tester).bottom - tester.getRect(find.text('Activity')).bottom,
        greaterThanOrEqualTo(300),
      );
      expect(tester.takeException(), isNull);
    },
  );

  testWidgets('on a tall window the feed fills the rest and nothing scrolls', (
    tester,
  ) async {
    await _pumpLiveSession(tester, const Size(1000, 1400));

    // The feed reaches the bottom of the window...
    final feed = tester.getRect(
      find
          .ancestor(
            of: find.text('Waiting for events…'),
            matching: find.byType(SizedBox),
          )
          .first,
    );
    expect(feed.bottom, _page(tester).bottom);
    expect(feed.height, greaterThan(320));
    // ...and the page has nothing to scroll.
    final position = tester
        .state<ScrollableState>(
          find
              .descendant(
                of: find.byType(CustomScrollView),
                matching: find.byType(Scrollable),
              )
              .first,
        )
        .position;
    expect(position.maxScrollExtent, 0);
    expect(tester.takeException(), isNull);
  });

  testWidgets('the join code sits in the header row, centred between the '
      'class and End session', (tester) async {
    // Wide, for the test font's square glyphs.
    await _pumpLiveSession(tester, const Size(2000, 1000));

    final label = tester.getRect(find.text('Join code'));
    final code = tester.getRect(find.text(_code));
    final copy = tester.getRect(find.widgetWithIcon(IconButton, Icons.copy));
    final spec = tester.getRect(find.text('Session 2026-05-26 09:15'));
    final headline = tester.getRect(find.text('6EWI'));
    final end = tester.getRect(
      find.widgetWithText(OutlinedButton, 'End session'),
    );

    expect(label.top, lessThan(headline.bottom));
    expect(code.top, lessThan(headline.bottom));
    final classRight = math.max(spec.right, headline.right);
    expect(code.left, greaterThan(classRight));
    expect(copy.right, lessThanOrEqualTo(end.left));
    expect(
      (code.left + copy.right) / 2,
      moreOrLessEquals((classRight + end.left) / 2, epsilon: 0.5),
    );
    expect(tester.takeException(), isNull);
  });

  testWidgets('on a narrow window the join code drops under the title', (
    tester,
  ) async {
    await _pumpLiveSession(tester, const Size(1000, 1000));

    final label = tester.getRect(find.text('Join code'));
    final code = tester.getRect(find.text(_code));
    final copy = tester.getRect(find.widgetWithIcon(IconButton, Icons.copy));
    final headline = tester.getRect(find.text('6EWI'));
    final end = tester.getRect(
      find.widgetWithText(OutlinedButton, 'End session'),
    );

    expect(label.top, greaterThan(headline.bottom));
    expect(code.left, headline.left);
    expect(end.top, lessThan(headline.bottom));
    expect(copy.right, lessThanOrEqualTo(end.left));
    expect(tester.takeException(), isNull);
  });

  testWidgets('the copy button in the header copies the code', (tester) async {
    final copied = <String>[];
    tester.binding.defaultBinaryMessenger.setMockMethodCallHandler(
      SystemChannels.platform,
      (call) async {
        if (call.method == 'Clipboard.setData') {
          copied.add((call.arguments as Map)['text'] as String);
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
    await _pumpLiveSession(tester, const Size(2000, 1000));

    await tester.tap(find.byTooltip('Copy code'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 500));

    expect(copied, [_code]);
    expect(find.text('Copied $_code'), findsOneWidget);
  });

  testWidgets('the code leaves the header once the session ends', (
    tester,
  ) async {
    final page = await _pumpLiveSession(tester, const Size(1000, 500));
    expect(find.text(_code), findsOneWidget);

    page.hub.emit('SessionEnded', {'sessionId': _sessionId});
    await tester.pumpAndSettle();

    expect(find.text('Session ended — event stream stopped.'), findsOneWidget);
    expect(find.text(_code), findsNothing);
    expect(find.byTooltip('Copy code'), findsNothing);
    expect(tester.takeException(), isNull);
  });
}
