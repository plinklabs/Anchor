import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/auth_token_store.dart';
import 'package:anchor_dashboard/api/bundles_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/pages/session_page.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';

// #126: pressing the session page's back arrow while the session is still
// running must ask the teacher to end it or leave it running — silently popping
// home is what orphaned the session in the first place.

ApiClient _dummyClient() => ApiClient(
  baseUrl: Uri.parse('http://localhost'),
  tokenProvider: () async => null,
);

class _FakeBundles extends BundlesApi {
  _FakeBundles() : super(_dummyClient());

  @override
  Future<List<BundleSummary>> list({bool includeArchived = false}) async =>
      const [];
}

class _FakeSessions extends SessionsApi {
  _FakeSessions() : super(_dummyClient());

  final List<String> endedSessionIds = [];

  @override
  Future<SessionDetail> getSession(String sessionId) async => SessionDetail(
    id: sessionId,
    classId: 'c1',
    className: 'Class',
    joinCode: '',
    startedAt: DateTime(2026, 6, 11, 9, 15),
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

  @override
  Future<void> endSession(String sessionId) async {
    endedSessionIds.add(sessionId);
  }
}

Future<_FakeSessions> _pumpSession(WidgetTester tester) async {
  final sessions = _FakeSessions();
  final router = GoRouter(
    initialLocation: '/session/s1',
    routes: [
      GoRoute(
        path: '/',
        builder: (_, _) => const Scaffold(body: Text('HOME')),
      ),
      GoRoute(
        path: '/session/:id',
        builder: (_, state) => SessionPage(
          sessionId: state.pathParameters['id']!,
          tokens: AuthTokenStore(),
          sessions: sessions,
          bundles: _FakeBundles(),
          apiBaseUrl: Uri.parse('http://localhost'),
        ),
      ),
    ],
  );

  await tester.pumpWidget(
    MaterialApp.router(
      // NoSplash dodges the InkSparkle shader this SDK's test harness can't
      // decode; bounded pumps (never pumpAndSettle) keep the real hub's retries
      // from stalling the test.
      theme: ThemeData(splashFactory: NoSplash.splashFactory),
      routerConfig: router,
    ),
  );
  // Flush _bootstrap's getSession + the setState that stores the detail.
  await tester.pump();
  await tester.pump();
  return sessions;
}

Future<void> _openExitDialog(WidgetTester tester) async {
  await tester.tap(find.byIcon(Icons.arrow_back));
  await tester.pump();
  await tester.pump(const Duration(milliseconds: 300));
}

Finder _inDialog(String text) =>
    find.descendant(of: find.byType(AlertDialog), matching: find.text(text));

void main() {
  testWidgets(
    'back arrow on an active session prompts end vs leave running (#126)',
    (tester) async {
      await _pumpSession(tester);

      await _openExitDialog(tester);

      expect(find.byType(AlertDialog), findsOneWidget);
      expect(_inDialog('End session'), findsOneWidget);
      expect(_inDialog('Leave running'), findsOneWidget);
      expect(_inDialog('Cancel'), findsOneWidget);
    },
  );

  testWidgets('choosing End session ends it and returns home (#126)', (
    tester,
  ) async {
    final sessions = await _pumpSession(tester);

    await _openExitDialog(tester);
    await tester.tap(_inDialog('End session'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 300));

    expect(sessions.endedSessionIds, contains('s1'));
    expect(find.text('HOME'), findsOneWidget);
  });

  testWidgets('choosing Leave running goes home without ending (#126)', (
    tester,
  ) async {
    final sessions = await _pumpSession(tester);

    await _openExitDialog(tester);
    await tester.tap(_inDialog('Leave running'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 300));

    expect(sessions.endedSessionIds, isEmpty);
    expect(find.text('HOME'), findsOneWidget);
  });

  testWidgets('Cancel keeps the teacher on the session, nothing ended (#126)', (
    tester,
  ) async {
    final sessions = await _pumpSession(tester);

    await _openExitDialog(tester);
    await tester.tap(_inDialog('Cancel'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 300));

    expect(find.text('HOME'), findsNothing);
    expect(find.byType(AlertDialog), findsNothing);
    expect(sessions.endedSessionIds, isEmpty);
  });
}
