import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/auth_token_store.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/l10n/app_localizations.dart';
import 'package:anchor_dashboard/pages/home_page.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:plink_design_system/plink_design_system.dart';

// AD3 (#168): the redesigned home page. These guard the page in isolation
// (real router, fake network): the paper composer (the oversized Fraunces
// headline, the class picker, when the session ends (#447), and the single
// primary (magenta) Start action), the start→/session navigation, and the
// empty state when a teacher has no classes. The #126 resume/end behaviour
// keeps its own regression file.

ApiClient _dummyClient() => ApiClient(
  baseUrl: Uri.parse('http://localhost'),
  tokenProvider: () async => null,
);

/// The end of this lesson period and of the next one, as the backend would
/// offer them for a session starting now (#447).
final _thisPeriodEnd = DateTime(2026, 10, 26, 10, 5);
final _nextPeriodEnd = DateTime(2026, 10, 26, 10, 55);

SessionEndOptions _periods() => SessionEndOptions(
  thisPeriodEndsAt: _thisPeriodEnd,
  nextPeriodEndsAt: _nextPeriodEnd,
  latestEndsAt: DateTime.now().add(const Duration(hours: 4)),
);

class _FakeSessions extends SessionsApi {
  _FakeSessions({
    this.classesList = const [],
    this.startId = 'new-session',
    SessionEndOptions? endOptionsValue,
  }) : endOptionsValue = endOptionsValue ?? _periods(),
       super(_dummyClient());

  final List<ClassSummary> classesList;
  final String startId;
  final List<String> startedForClass = [];

  /// What `endOptions` answers, per class id when [endOptionsByClass] has it.
  SessionEndOptions endOptionsValue;
  Map<String, SessionEndOptions> endOptionsByClass = {};
  final List<String> endOptionsCalls = [];

  /// How each started session was asked to end.
  final List<SessionEnd?> startedEnds = [];

  /// What startSession throws, when a test sets it.
  Object? startError;

  @override
  Future<SessionEndOptions> endOptions(String classId) async {
    endOptionsCalls.add(classId);
    return endOptionsByClass[classId] ?? endOptionsValue;
  }

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
    SessionEnd? end,
  }) async {
    final error = startError;
    if (error != null) throw error;
    startedForClass.add(classId);
    startedEnds.add(end);
    return StartSessionResponse(
      id: startId,
      classId: classId,
      joinCode: 'AB12',
      startedAt: DateTime(2026, 6, 14, 10),
    );
  }
}

Widget _host({required _FakeSessions sessions, AuthTokenStore? tokens}) {
  final home = HomePage(tokens: tokens ?? AuthTokenStore(), sessions: sessions);
  final router = GoRouter(
    initialLocation: '/',
    routes: <RouteBase>[
      GoRoute(path: '/', builder: (_, _) => home),
      GoRoute(
        path: '/session/:id',
        builder: (_, GoRouterState state) =>
            Scaffold(body: Text('SESSION ${state.pathParameters['id']}')),
      ),
    ],
  );
  return MaterialApp.router(
    localizationsDelegates: AppLocalizations.localizationsDelegates,
    supportedLocales: AppLocalizations.supportedLocales,
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

  testWidgets('the session ends at the end of this period by default, and '
      'Start asks for that (#447)', (tester) async {
    _bigWindow(tester);
    final sessions = _FakeSessions(classesList: [_math]);
    await tester.pumpWidget(_host(sessions: sessions));
    await tester.pumpAndSettle();

    expect(sessions.endOptionsCalls, ['c1']);
    expect(find.text('At the end of this period (10:05)'), findsOneWidget);
    // No time to type while a period end is chosen.
    expect(find.byKey(const Key('end-time')), findsNothing);

    await tester.tap(find.byKey(const Key('start-session')));
    await tester.pumpAndSettle();

    expect(sessions.startedEnds.single?.mode, 'ThisPeriod');
    expect(find.text('SESSION new-session'), findsOneWidget);
  });

  testWidgets('the teacher can end the session at the end of the next period '
      '(#447)', (tester) async {
    _bigWindow(tester);
    final sessions = _FakeSessions(classesList: [_math]);
    await tester.pumpWidget(_host(sessions: sessions));
    await tester.pumpAndSettle();

    await tester.tap(find.byKey(const Key('end-picker')));
    await tester.pumpAndSettle();
    // The three choices, each period end naming its time.
    expect(find.text('At the end of the next period (10:55)'), findsOneWidget);
    expect(find.text('At a time I pick'), findsOneWidget);
    await tester.tap(find.text('At the end of the next period (10:55)'));
    await tester.pumpAndSettle();

    await tester.tap(find.byKey(const Key('start-session')));
    await tester.pumpAndSettle();

    expect(sessions.startedEnds.single?.mode, 'NextPeriod');
  });

  testWidgets('without a lesson period only a time is offered, about a lesson '
      'away, and Start ends the session then (#447)', (tester) async {
    _bigWindow(tester);
    final sessions = _FakeSessions(
      classesList: [_math],
      endOptionsValue: SessionEndOptions(
        latestEndsAt: DateTime.now().add(const Duration(hours: 4)),
      ),
    );
    await tester.pumpWidget(_host(sessions: sessions));
    await tester.pumpAndSettle();

    expect(find.text('At a time I pick'), findsOneWidget);
    expect(find.textContaining('this period'), findsNothing);
    expect(find.byKey(const Key('end-time')), findsOneWidget);

    await tester.tap(find.byKey(const Key('end-picker')));
    await tester.pumpAndSettle();
    expect(find.textContaining('period'), findsNothing);
    // Close the menu again on its one item.
    await tester.tap(find.text('At a time I pick').last);
    await tester.pumpAndSettle();

    final before = DateTime.now();
    await tester.tap(find.byKey(const Key('start-session')));
    await tester.pumpAndSettle();

    final end = sessions.startedEnds.single!;
    expect(end.mode, 'SpecificTime');
    // Fifty minutes on, rounded up to five: between 50 and 55 minutes away.
    expect(
      end.at!.difference(before).inMinutes,
      inInclusiveRange(49, 55),
      reason: 'picked ${end.at}, started ${before.toIso8601String()}',
    );
  });

  testWidgets('a typed end time that is not a time, or too late, keeps Start '
      'disabled and says why (#447)', (tester) async {
    _bigWindow(tester);
    final now = DateTime.now();
    final latest = now.add(const Duration(minutes: 30));
    final sessions = _FakeSessions(
      classesList: [_math],
      endOptionsValue: SessionEndOptions(latestEndsAt: latest),
    );
    await tester.pumpWidget(_host(sessions: sessions));
    await tester.pumpAndSettle();

    await tester.enterText(find.byKey(const Key('end-time')), '11h30');
    await tester.pump();
    expect(find.text('Use a time like 11:30.'), findsOneWidget);
    expect(_startEnabled(tester), isFalse);

    final tooLate = now.add(const Duration(hours: 2));
    await tester.enterText(find.byKey(const Key('end-time')), _hhmm(tooLate));
    await tester.pump();
    expect(
      find.text('Pick a time no later than ${_hhmm(latest)}.'),
      findsOneWidget,
    );
    expect(_startEnabled(tester), isFalse);

    await tester.tap(find.byKey(const Key('start-session')));
    await tester.pumpAndSettle();
    expect(sessions.startedEnds, isEmpty);
  });

  testWidgets('switching class offers that class\'s periods (#447)', (
    tester,
  ) async {
    _bigWindow(tester);
    final sessions = _FakeSessions(
      classesList: [
        _math,
        ClassSummary(id: 'c2', name: 'Art 202', schoolYear: '2026'),
      ],
    )..endOptionsByClass = {'c2': SessionEndOptions()};
    await tester.pumpWidget(_host(sessions: sessions));
    await tester.pumpAndSettle();
    expect(find.text('At the end of this period (10:05)'), findsOneWidget);

    await tester.tap(find.byKey(const Key('class-picker')));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Art 202 (2026)').last);
    await tester.pumpAndSettle();

    expect(sessions.endOptionsCalls, ['c1', 'c2']);
    expect(find.text('At the end of this period (10:05)'), findsNothing);
    expect(find.text('At a time I pick'), findsOneWidget);
    expect(find.byKey(const Key('end-time')), findsOneWidget);
  });

  testWidgets('a period that ran out while the teacher chose says so and '
      'offers what applies now (#447)', (tester) async {
    _bigWindow(tester);
    final sessions = _FakeSessions(classesList: [_math])
      ..startError = ApiException(
        409,
        '{"error":"no lesson period","noLessonPeriod":true}',
      );
    await tester.pumpWidget(_host(sessions: sessions));
    await tester.pumpAndSettle();

    // By now the period is over: the backend offers only a time.
    sessions.endOptionsValue = SessionEndOptions();
    await tester.tap(find.byKey(const Key('start-session')));
    await tester.pumpAndSettle();

    expect(
      find.text(
        'The lesson period has changed in the meantime. Check when the '
        'session ends, then start it again.',
      ),
      findsOneWidget,
    );
    expect(sessions.endOptionsCalls, ['c1', 'c1']);
    expect(find.text('At a time I pick'), findsOneWidget);
    expect(find.textContaining('SESSION'), findsNothing);
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

final _math = ClassSummary(id: 'c1', name: 'Math 101', schoolYear: '2026');

void _bigWindow(WidgetTester tester) {
  tester.view.physicalSize = const Size(1200, 900);
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.resetPhysicalSize);
  addTearDown(tester.view.resetDevicePixelRatio);
}

bool _startEnabled(WidgetTester tester) =>
    tester
        .widget<ElevatedButton>(find.byKey(const Key('start-session')))
        .onPressed !=
    null;

String _hhmm(DateTime at) =>
    '${at.hour.toString().padLeft(2, '0')}:'
    '${at.minute.toString().padLeft(2, '0')}';
