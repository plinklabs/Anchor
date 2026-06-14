import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/pages/history_page.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:plink_design_system/plink_design_system.dart';

// Paper redesign of the history list (AD7, #172). Verifies the brand contracts
// the redesign introduced:
//  - past sessions render as hairline rows (no Material ListTile / Card / Divider);
//  - the page is paper;
//  - each row carries a muted "Ended" outline badge — the archived counterpart
//    of the live page's magenta LIVE spark — and the page paints no magenta;
//  - "Load more" stays calm ink (OutlinedButton, never the spark).

ApiClient _dummyClient() => ApiClient(
  baseUrl: Uri.parse('http://localhost'),
  tokenProvider: () async => null,
);

class _FakeSessions extends SessionsApi {
  _FakeSessions(this._entries) : super(_dummyClient());
  final List<SessionHistoryEntry> _entries;

  String? lastNavigatedId;

  @override
  Future<List<SessionHistoryEntry>> history({int limit = 50, int offset = 0}) async {
    if (offset > 0) return const <SessionHistoryEntry>[];
    return _entries;
  }
}

SessionHistoryEntry _entry(String id, String className) => SessionHistoryEntry(
  id: id,
  classId: 'c-$id',
  className: className,
  startedAt: DateTime(2026, 6, 12, 9, 15),
  endedAt: DateTime(2026, 6, 12, 10, 5),
);

void main() {
  Widget host(SessionsApi sessions, {void Function(String id)? onTap}) {
    final router = GoRouter(
      routes: [
        GoRoute(
          path: '/',
          builder: (_, _) => HistoryPage(sessions: sessions),
        ),
        GoRoute(
          path: '/history/:id',
          builder: (_, state) {
            onTap?.call(state.pathParameters['id']!);
            return const Scaffold(body: Text('past-session-stub'));
          },
        ),
      ],
    );
    return MaterialApp.router(
      theme: PlinkTheme.paper,
      routerConfig: router,
    );
  }

  testWidgets(
    'past sessions render as hairline rows with an "Ended" outline badge — '
    'no Material ListTile / Card / Divider',
    (tester) async {
      tester.view.physicalSize = const Size(1200, 900);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);

      await tester.pumpWidget(
        host(_FakeSessions([_entry('1', 'Math 101'), _entry('2', 'History 7')])),
      );
      await tester.pumpAndSettle();

      // The archive is paper, separated by hairlines — never Material chrome.
      expect(find.byType(ListTile), findsNothing);
      expect(find.byType(Card), findsNothing);
      expect(find.byType(Divider), findsNothing);

      // Sessions read by class name.
      expect(find.text('Math 101'), findsOneWidget);
      expect(find.text('History 7'), findsOneWidget);

      // Each row wears the muted "Ended" outline badge (PlinkBadge upper-cases).
      expect(find.widgetWithText(PlinkBadge, 'ENDED'), findsNWidgets(2));

      expect(tester.takeException(), isNull);
    },
  );

  testWidgets('the archive paints no magenta spark — "Load more" stays calm ink',
      (tester) async {
    tester.view.physicalSize = const Size(1200, 900);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);

    // 50 entries => the page is not exhausted, so the "Load more" affordance
    // shows. It must be calm ink, never the magenta ElevatedButton spark.
    final entries = [for (var i = 0; i < 50; i++) _entry('$i', 'Class $i')];
    await tester.pumpWidget(host(_FakeSessions(entries)));
    await tester.pumpAndSettle();

    // The trailing affordance sits below the fold — scroll it into view.
    await tester.scrollUntilVisible(
      find.text('Load more'),
      300,
      scrollable: find.byType(Scrollable).first,
    );
    await tester.pumpAndSettle();

    expect(find.widgetWithText(OutlinedButton, 'Load more'), findsOneWidget);
    // The constructive/magenta ElevatedButton never appears in the archive.
    expect(find.byType(ElevatedButton), findsNothing);

    expect(tester.takeException(), isNull);
  });

  testWidgets('the empty state reads as a quiet mono note, not a Material card',
      (tester) async {
    await tester.pumpWidget(host(_FakeSessions(const [])));
    await tester.pumpAndSettle();

    expect(
      find.text('No past sessions yet. Sessions appear here after you end them.'),
      findsOneWidget,
    );
    expect(find.byType(Card), findsNothing);
    expect(tester.takeException(), isNull);
  });

  testWidgets('tapping a row opens its read-only review at /history/:id',
      (tester) async {
    tester.view.physicalSize = const Size(1200, 900);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);

    String? tapped;
    await tester.pumpWidget(
      host(_FakeSessions([_entry('abc', 'Math 101')]), onTap: (id) => tapped = id),
    );
    await tester.pumpAndSettle();

    await tester.tap(find.text('Math 101'));
    await tester.pumpAndSettle();

    expect(tapped, 'abc');
    expect(find.text('past-session-stub'), findsOneWidget);
  });
}
