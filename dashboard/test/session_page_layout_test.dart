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
// row is too narrow.
//
// #494: on a window at least 900 px wide the students fill a column of their
// own on the left, and the requests, the bundles and the feed share the
// right, each column scrolling on its own. Narrower, and once the session has
// ended, the panels stack as #422 left them.
//
// The real-font, real-window runs are in
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

const _sixStudents = ['Ada', 'Bo', 'Cas', 'Dina', 'Eli', 'Fien'];

/// A class of [count], named so they sort in this order: S01, S02, …
List<String> _students(int count) => [
  for (var i = 1; i <= count; i++) 'S${i.toString().padLeft(2, '0')}',
];

class _FakeSessions extends SessionsApi {
  _FakeSessions({this.students = _sixStudents}) : super(_dummyClient());

  final List<String> students;
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
      for (final name in students)
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
  Size window, {
  List<String> students = _sixStudents,
}) async {
  tester.view.physicalSize = window;
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.resetPhysicalSize);
  addTearDown(tester.view.resetDevicePixelRatio);

  final hub = _StubHub();
  final sessions = _FakeSessions(students: students);
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

/// The part of the window the page scrolls in: the outermost scroll view, as
/// on a wide window the right column has one of its own (#494).
Rect _page(WidgetTester tester) =>
    tester.getRect(find.byType(CustomScrollView).first);

bool _inView(WidgetTester tester, Finder finder) {
  // A row a lazy list hasn't built is nowhere near the window.
  if (finder.evaluate().isEmpty) return false;
  final rect = tester.getRect(finder);
  return rect.top >= _page(tester).top && rect.bottom <= _page(tester).bottom;
}

/// Turns the mouse wheel by [dy] at [at].
Future<void> _wheelAt(WidgetTester tester, Offset at, double dy) async {
  final pointer = TestPointer(1, PointerDeviceKind.mouse)..hover(at);
  await tester.sendEventToBinding(pointer.scroll(Offset(0, dy)));
  await tester.pumpAndSettle();
}

/// Turns the mouse wheel by [dy] over the page's left edge, outside the
/// roster's and the requests' own lists, so the page itself scrolls.
Future<void> _wheel(WidgetTester tester, double dy) =>
    _wheelAt(tester, _page(tester).bottomLeft + const Offset(12, -40), dy);

/// The page's scroll position: the outermost one.
ScrollPosition _pageScroll(WidgetTester tester) => tester
    .state<ScrollableState>(
      find
          .descendant(
            of: find.byType(CustomScrollView).first,
            matching: find.byType(Scrollable),
          )
          .first,
    )
    .position;

/// The roster's list, the one around [student]'s row.
Rect _rosterList(WidgetTester tester, String student) => tester.getRect(
  find.ancestor(of: find.text(student), matching: find.byType(ListView)).first,
);

void main() {
  testWidgets(
    'on a short window the page scrolls to every panel and the feed',
    (tester) async {
      // Narrower than the two columns (#494): the panels stack over the feed.
      // At 1000 px wide this ran before #494; that width lays the page out in
      // columns now, which the #494 tests below cover.
      final page = await _pumpLiveSession(tester, const Size(860, 500));

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
      expect(
        _inView(tester, find.text("Ada's agent stopped reporting.")),
        isTrue,
      );
      expect(
        _page(tester).bottom - tester.getRect(find.text('Activity')).bottom,
        greaterThanOrEqualTo(300),
      );
      expect(tester.takeException(), isNull);
    },
  );

  for (final (layout, window) in [
    ('stacked', const Size(860, 1400)),
    ('in columns (#494)', const Size(1000, 1400)),
  ]) {
    testWidgets(
      'on a tall window the feed fills the rest and nothing scrolls, $layout',
      (tester) async {
        await _pumpLiveSession(tester, window);

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
        expect(_pageScroll(tester).maxScrollExtent, 0);
        expect(tester.takeException(), isNull);
      },
    );
  }

  group('on a wide window the students get the left column (#494)', () {
    testWidgets('the whole class shows at once on the left; the requests, the '
        'bundles and the feed share the right', (tester) async {
      final page = await _pumpLiveSession(
        tester,
        const Size(1400, 1000),
        students: _students(18),
      );

      // Every student, in view, no box of 240 px to scroll in.
      final students = find.text('Students (18/18 in session)');
      for (final name in _students(18)) {
        expect(_inView(tester, find.text(name)), isTrue, reason: name);
      }
      final roster = _rosterList(tester, 'S01');
      expect(roster.height, greaterThan(240));
      expect(roster.bottom, _page(tester).bottom);

      // The right column: the requests on top, then the bundles, then the
      // feed down to the bottom of the window, all right of the students.
      final requests = tester.getRect(find.text('Pending requests'));
      final bundles = tester.getRect(find.text('Allowed bundles'));
      final activity = tester.getRect(find.text('Activity'));
      expect(roster.right, lessThan(requests.left));
      expect(requests.left, bundles.left);
      expect(bundles.left, activity.left);
      expect(requests.top, tester.getRect(students).top);
      expect(requests.bottom, lessThan(bundles.top));
      expect(bundles.bottom, lessThan(activity.top));
      final feed = tester.getRect(
        find
            .ancestor(
              of: find.text('Waiting for events…'),
              matching: find.byType(SizedBox),
            )
            .first,
      );
      expect(feed.bottom, _page(tester).bottom);
      expect(feed.left, greaterThanOrEqualTo(roster.right));
      expect(_pageScroll(tester).maxScrollExtent, 0);

      // The bundles toggle and the feed lands its events, as ever.
      await tester.tap(find.widgetWithText(FilterChip, 'Math'));
      await tester.pumpAndSettle();
      expect(page.sessions.updateCalls, [
        ['b1'],
      ]);
      page.hub.emit('HeartbeatLost', {
        'sessionId': _sessionId,
        'userId': 'S01',
      });
      await tester.pumpAndSettle();
      expect(
        _inView(tester, find.text("S01's agent stopped reporting.")),
        true,
      );
      expect(tester.takeException(), isNull);
    });

    testWidgets('a long class scrolls in its column, and the right column on '
        'its own', (tester) async {
      await _pumpLiveSession(
        tester,
        const Size(1400, 700),
        students: _students(40),
      );
      expect(tester.takeException(), isNull);

      final first = find.text('S01');
      final last = find.text('S40', skipOffstage: false);
      final requests = find.text('Pending requests');
      final requestsTop = tester.getRect(requests).top;
      final header = tester.getRect(find.text('6EWI'));
      expect(_inView(tester, last), isFalse, reason: 'below the window');

      // The wheel over the students scrolls their list down to the last one;
      // the right column and the header stay put.
      final rosterAt = tester.getCenter(first);
      for (var i = 0; i < 40 && !_inView(tester, last); i++) {
        await _wheelAt(tester, rosterAt, 80);
      }
      expect(_inView(tester, last), isTrue);
      expect(tester.getRect(requests).top, requestsTop);
      expect(tester.getRect(find.text('6EWI')), header);

      // The wheel over the right column scrolls it down to the feed, under
      // the requests and the bundles; the students stay put.
      final rosterTop = tester.getRect(last).top;
      final activity = find.text('Activity');
      final activityTop = tester.getRect(activity).top;
      await _wheelAt(tester, tester.getCenter(requests), 5000);
      expect(tester.getRect(activity).top, lessThan(activityTop));
      expect(_inView(tester, activity), isTrue);
      expect(tester.getRect(last).top, rosterTop);
      expect(tester.takeException(), isNull);
    });

    testWidgets('below the breakpoint the panels stack over the feed, the '
        'roster capped as before', (tester) async {
      await _pumpLiveSession(
        tester,
        const Size(899, 1400),
        students: _students(18),
      );

      final students = tester.getRect(find.text('Students (18/18 in session)'));
      final requests = tester.getRect(find.text('Pending requests'));
      final bundles = tester.getRect(find.text('Allowed bundles'));
      final activity = tester.getRect(find.text('Activity'));
      // One column: the requests, the students, the bundles, then the feed.
      for (final rect in [students, bundles, activity]) {
        expect(rect.left, requests.left);
      }
      expect(requests.bottom, lessThan(students.top));
      expect(students.bottom, lessThan(bundles.top));
      expect(bundles.bottom, lessThan(activity.top));
      expect(_rosterList(tester, 'S01').height, lessThanOrEqualTo(240));
      expect(tester.takeException(), isNull);
    });

    testWidgets('the layout follows the window across the breakpoint', (
      tester,
    ) async {
      await _pumpLiveSession(tester, const Size(1400, 1000));
      double top(String text) => tester.getRect(find.text(text)).top;
      expect(top('Pending requests'), top('Students (6/6 in session)'));

      tester.view.physicalSize = const Size(860, 1000);
      await tester.pumpAndSettle();
      expect(tester.takeException(), isNull);
      expect(
        top('Pending requests'),
        lessThan(top('Students (6/6 in session)')),
      );
      expect(find.text('chat.example.com'), findsOneWidget);

      tester.view.physicalSize = const Size(1400, 1000);
      await tester.pumpAndSettle();
      expect(tester.takeException(), isNull);
      expect(top('Pending requests'), top('Students (6/6 in session)'));
    });
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
    // Wide as the window is, the ended view is one column (#494): the
    // students and the bundles go, the feed spans the page under the notice.
    expect(find.text('Students (6/6 in session)'), findsNothing);
    expect(find.text('Allowed bundles'), findsNothing);
    expect(
      tester.getRect(find.text('Activity')).left,
      tester.getRect(find.text('Session ended — event stream stopped.')).left,
    );
    expect(tester.takeException(), isNull);
  });
}
