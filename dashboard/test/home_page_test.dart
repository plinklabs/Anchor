import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/auth_token_store.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/pages/home_page.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:plink_design_system/plink_design_system.dart';

// AD3 (#168): the redesigned home page. These guard the page in isolation
// (real router, fake network): the paper composer (the oversized Fraunces
// headline, the class picker, and the single primary (magenta) Start action),
// the start→/session navigation, and the empty state when a teacher has no
// classes. The #126 resume/end behaviour keeps its own regression file.

ApiClient _dummyClient() => ApiClient(
  baseUrl: Uri.parse('http://localhost'),
  tokenProvider: () async => null,
);

class _FakeSessions extends SessionsApi {
  _FakeSessions({
    this.classesList = const [],
    this.startId = 'new-session',
  }) : super(_dummyClient());

  final List<ClassSummary> classesList;
  final String startId;
  final List<String> startedForClass = [];

  @override
  Future<MeResponse> me() async =>
      MeResponse(id: 'u1', displayName: 'Teacher', role: 'teacher');

  @override
  Future<List<ClassSummary>> classes() async => classesList;

  @override
  Future<List<ActiveSession>> activeSessions() async => const [];

  @override
  Future<StartSessionResponse> startSession(
    String classId, {
    List<String> bundleIds = const <String>[],
  }) async {
    startedForClass.add(classId);
    return StartSessionResponse(
      id: startId,
      classId: classId,
      joinCode: 'AB12',
      startedAt: DateTime(2026, 6, 14, 10),
    );
  }
}

Widget _host({required _FakeSessions sessions, AuthTokenStore? tokens}) {
  final home = HomePage(
    tokens: tokens ?? AuthTokenStore(),
    sessions: sessions,
  );
  final router = GoRouter(
    initialLocation: '/',
    routes: <RouteBase>[
      GoRoute(path: '/', builder: (_, _) => home),
      GoRoute(
        path: '/session/:id',
        builder: (_, GoRouterState state) => Scaffold(
          body: Text('SESSION ${state.pathParameters['id']}'),
        ),
      ),
    ],
  );
  return MaterialApp.router(
    theme: PlinkTheme.paper.copyWith(
      extensions: const <ThemeExtension<dynamic>>[
        PlinkProductAccent(Color(0xFF34357A)),
      ],
    ),
    routerConfig: router,
  );
}

void main() {
  testWidgets('renders the paper composer: headline, picker, and start spark', (
    tester,
  ) async {
    tester.view.physicalSize = const Size(1200, 900);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);

    final sessions = _FakeSessions(
      classesList: <ClassSummary>[
        ClassSummary(id: 'c1', name: 'Math 101', schoolYear: '2026'),
      ],
    );
    await tester.pumpWidget(_host(sessions: sessions));
    await tester.pumpAndSettle();

    // The one oversized Fraunces line, the picker, and the single primary
    // action naming the selected class.
    expect(find.byKey(const Key('home-headline')), findsOneWidget);
    expect(find.byKey(const Key('class-picker')), findsOneWidget);
    expect(
      find.widgetWithText(ElevatedButton, 'Start session for Math 101'),
      findsOneWidget,
    );

    // The Start button is the magenta spark — it inherits the DS theme spark
    // with no inline override.
    final ButtonStyle? style = tester
        .widget<ElevatedButton>(find.byKey(const Key('start-session')))
        .style;
    expect(style?.backgroundColor, isNull);
    expect(PlinkTheme.paper.colorScheme.primary, PlinkColors.magenta);

    // No overflow with the real fonts at this size.
    expect(tester.takeException(), isNull);
  });

  testWidgets('tapping Start opens the newly created session', (tester) async {
    tester.view.physicalSize = const Size(1200, 900);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);

    final sessions = _FakeSessions(
      classesList: <ClassSummary>[
        ClassSummary(id: 'c1', name: 'Math 101', schoolYear: '2026'),
      ],
      startId: 'sess-9',
    );
    await tester.pumpWidget(_host(sessions: sessions));
    await tester.pumpAndSettle();

    await tester.tap(find.byKey(const Key('start-session')));
    await tester.pumpAndSettle();

    // The class was started and the app navigated into the live session.
    expect(sessions.startedForClass, contains('c1'));
    expect(find.text('SESSION sess-9'), findsOneWidget);
  });

  testWidgets('no classes assigned shows the empty state, no start button', (
    tester,
  ) async {
    tester.view.physicalSize = const Size(1200, 900);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);

    await tester.pumpWidget(_host(sessions: _FakeSessions()));
    await tester.pumpAndSettle();

    expect(find.text('No classes assigned to you yet.'), findsOneWidget);
    expect(find.byKey(const Key('start-session')), findsNothing);
    expect(find.byKey(const Key('home-headline')), findsOneWidget);
  });
}
