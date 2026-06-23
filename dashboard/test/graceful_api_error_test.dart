import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/auth_token_store.dart';
import 'package:anchor_dashboard/api/classes_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/pages/classes_page.dart';
import 'package:anchor_dashboard/pages/history_page.dart';
import 'package:anchor_dashboard/pages/home_page.dart';
import 'package:anchor_dashboard/widgets/api_error_text.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:plink_design_system/plink_design_system.dart';

// #278: a teacher whose account isn't provisioned with the Teacher role hits a
// 403 on the role-gated endpoints. The dashboard must never dump the raw
// `ApiException(403):` into the UI — it maps that to a calm, actionable notice
// ("Your account isn't set up as a teacher yet…"), and keeps the genuine empty
// state ("No classes assigned to you yet") for the 200-but-empty case. These
// guard the shared helper and each of the three surfaces (Home, History,
// Classes) end-to-end against a 403.

const String _notAuthorizedFragment = "isn't set up as a teacher yet";

ApiClient _dummyClient() => ApiClient(
  baseUrl: Uri.parse('http://localhost'),
  tokenProvider: () async => null,
);

class _ThrowingHomeSessions extends SessionsApi {
  _ThrowingHomeSessions(this.error) : super(_dummyClient());
  final Object error;

  @override
  Future<MeResponse> me() async =>
      MeResponse(id: 'u1', displayName: 'Teacher', role: 'teacher');

  @override
  Future<List<ClassSummary>> classes() async => throw error;

  @override
  Future<List<ActiveSession>> activeSessions() async => const [];
}

class _ThrowingHistorySessions extends SessionsApi {
  _ThrowingHistorySessions(this.error) : super(_dummyClient());
  final Object error;

  @override
  Future<List<SessionHistoryEntry>> history({int limit = 50, int offset = 0}) async =>
      throw error;
}

class _ThrowingClassesSessions extends SessionsApi {
  _ThrowingClassesSessions(this.error) : super(_dummyClient());
  final Object error;

  @override
  Future<List<ClassSummary>> classes() async => throw error;
}

class _FakeClassesApi extends ClassesApi {
  _FakeClassesApi() : super(_dummyClient());

  @override
  Future<List<String>> schools() async => const <String>[];
}

Widget _homeHost(SessionsApi sessions) {
  final router = GoRouter(
    initialLocation: '/',
    routes: <RouteBase>[
      GoRoute(
        path: '/',
        builder: (_, _) => HomePage(tokens: AuthTokenStore(), sessions: sessions),
      ),
      GoRoute(
        path: '/session/:id',
        builder: (_, state) =>
            Scaffold(body: Text('SESSION ${state.pathParameters['id']}')),
      ),
    ],
  );
  return MaterialApp.router(theme: PlinkTheme.paper, routerConfig: router);
}

Widget _historyHost(SessionsApi sessions) {
  final router = GoRouter(
    routes: [GoRoute(path: '/', builder: (_, _) => HistoryPage(sessions: sessions))],
  );
  return MaterialApp.router(theme: PlinkTheme.paper, routerConfig: router);
}

Widget _classesHost(SessionsApi sessions, ClassesApi classes) => MaterialApp(
  theme: PlinkTheme.paper,
  home: ClassesPage(sessions: sessions, classes: classes),
);

void _bigWindow(WidgetTester tester) {
  tester.view.physicalSize = const Size(1400, 1000);
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.resetPhysicalSize);
  addTearDown(tester.view.resetDevicePixelRatio);
}

void main() {
  group('describeApiError', () {
    test('maps a 403 to the calm not-authorized notice', () {
      final msg = describeApiError(ApiException(403, ''), generic: 'boom');
      expect(msg.isAuthorization, isTrue);
      expect(msg.text, contains(_notAuthorizedFragment));
      // Never leaks the raw exception toString().
      expect(msg.text, isNot(contains('ApiException')));
    });

    test('falls back to the generic message for a non-403 ApiException', () {
      final msg = describeApiError(ApiException(500, 'stacktrace'), generic: 'boom');
      expect(msg.isAuthorization, isFalse);
      expect(msg.text, 'boom');
    });

    test('falls back to the generic message for a non-API error', () {
      final msg = describeApiError(Exception('socket'), generic: 'boom');
      expect(msg.isAuthorization, isFalse);
      expect(msg.text, 'boom');
    });
  });

  testWidgets('Home: a 403 shows the not-authorized notice, not the raw '
      'exception nor the empty state', (tester) async {
    _bigWindow(tester);
    await tester.pumpWidget(_homeHost(_ThrowingHomeSessions(ApiException(403, ''))));
    await tester.pumpAndSettle();

    expect(find.textContaining(_notAuthorizedFragment), findsOneWidget);
    expect(find.textContaining('ApiException'), findsNothing);
    // The genuine empty state is decoupled from the error path.
    expect(find.text('No classes assigned to you yet.'), findsNothing);

    // The notice reads as calm ink, not the red error colour.
    final notice = tester.widget<Text>(find.textContaining(_notAuthorizedFragment));
    expect(notice.style?.color, PlinkColors.ink60);
  });

  testWidgets('Home: a non-403 failure shows the human generic message in error '
      'colour, never the raw exception', (tester) async {
    _bigWindow(tester);
    await tester.pumpWidget(
      _homeHost(_ThrowingHomeSessions(ApiException(500, 'kaboom'))),
    );
    await tester.pumpAndSettle();

    expect(
      find.text('Could not load start-session data. Please try again.'),
      findsOneWidget,
    );
    expect(find.textContaining('ApiException'), findsNothing);
    expect(find.textContaining('kaboom'), findsNothing);

    final err = tester.widget<Text>(
      find.text('Could not load start-session data. Please try again.'),
    );
    expect(err.style?.color, PlinkTheme.paper.colorScheme.error);
  });

  testWidgets('History: a 403 shows the not-authorized notice, not the raw '
      'exception', (tester) async {
    _bigWindow(tester);
    await tester.pumpWidget(
      _historyHost(_ThrowingHistorySessions(ApiException(403, ''))),
    );
    await tester.pumpAndSettle();

    expect(find.textContaining(_notAuthorizedFragment), findsOneWidget);
    expect(find.textContaining('ApiException'), findsNothing);
  });

  testWidgets('Classes: a 403 surfaces the not-authorized notice in place of '
      '"Pick a class", not the raw exception', (tester) async {
    _bigWindow(tester);
    await tester.pumpWidget(
      _classesHost(_ThrowingClassesSessions(ApiException(403, '')), _FakeClassesApi()),
    );
    await tester.pumpAndSettle();

    expect(find.textContaining(_notAuthorizedFragment), findsOneWidget);
    expect(find.textContaining('ApiException'), findsNothing);
    // A failed load isn't dressed up as a genuine empty roster.
    expect(find.text('Pick a class on the left.'), findsNothing);
    expect(
      find.text('No classes you teach. Create one with "New class".'),
      findsNothing,
    );
  });
}
