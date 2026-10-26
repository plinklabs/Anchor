import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/schools_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart' show ApiException;
import 'package:anchor_dashboard/l10n/app_localizations.dart';
import 'package:anchor_dashboard/pages/manage_schools_page.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

// Widget tests for the "Schools" admin sub-tab (#301): they drive the page
// against a scripted fake SchoolsApi to prove the list renders with each
// school's active state and number of lesson periods, toggling a switch calls
// through and updates the row, the lesson-period editor (#447) checks and
// saves a timetable, and an API failure surfaces an inline error.
// Layout/font/composition is covered by the integration test.

ApiClient _dummyClient() => ApiClient(
  baseUrl: Uri.parse('http://localhost'),
  tokenProvider: () async => null,
);

School _school(
  String name, {
  bool isActive = true,
  List<LessonPeriod> lessonPeriods = const [],
}) => School(name: name, isActive: isActive, lessonPeriods: lessonPeriods);

class _FakeSchools extends SchoolsApi {
  _FakeSchools({
    required this.schools,
    this.setActiveThrows,
    this.listThrows,
    this.setLessonPeriodsThrows,
  }) : super(_dummyClient());

  List<School> schools;
  Object? setActiveThrows;
  Object? listThrows;
  Object? setLessonPeriodsThrows;

  final List<(String, bool)> calls = [];
  final List<String> lessonPeriodCalls = [];

  @override
  Future<School> setLessonPeriods(
    String name,
    List<LessonPeriod> periods,
  ) async {
    if (setLessonPeriodsThrows != null) throw setLessonPeriodsThrows!;
    lessonPeriodCalls.add(
      '$name: ${[for (final p in periods) '${p.start}-${p.end}'].join(', ')}',
    );
    final current = schools.firstWhere((s) => s.name == name);
    final updated = _school(
      name,
      isActive: current.isActive,
      lessonPeriods: periods,
    );
    schools = [
      for (final s in schools)
        if (s.name == name) updated else s,
    ];
    return updated;
  }

  @override
  Future<List<School>> listSchools() async {
    if (listThrows != null) throw listThrows!;
    return schools;
  }

  @override
  Future<School> setActive(String name, bool isActive) async {
    if (setActiveThrows != null) throw setActiveThrows!;
    calls.add((name, isActive));
    final updated = _school(name, isActive: isActive);
    schools = [
      for (final s in schools)
        if (s.name == name) updated else s,
    ];
    return updated;
  }
}

Widget _host(SchoolsApi schools) => MaterialApp(
  localizationsDelegates: AppLocalizations.localizationsDelegates,
  supportedLocales: AppLocalizations.supportedLocales,
  home: ManageSchoolsPage(schools: schools),
);

void _bigWindow(WidgetTester tester) {
  tester.view.physicalSize = const Size(1400, 1000);
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.resetPhysicalSize);
  addTearDown(tester.view.resetDevicePixelRatio);
}

void main() {
  testWidgets('lists schools with their active state on load', (tester) async {
    _bigWindow(tester);
    final api = _FakeSchools(
      schools: [
        _school('Sint-Jan', isActive: true),
        _school('Sint-Maria', isActive: false),
      ],
    );

    await tester.pumpWidget(_host(api));
    await tester.pumpAndSettle();

    expect(find.text('Sint-Jan'), findsOneWidget);
    expect(find.text('Sint-Maria'), findsOneWidget);
    expect(find.byKey(const Key('school-toggle-Sint-Jan')), findsOneWidget);

    final active = tester.widget<Switch>(
      find.byKey(const Key('school-toggle-Sint-Jan')),
    );
    final inactive = tester.widget<Switch>(
      find.byKey(const Key('school-toggle-Sint-Maria')),
    );
    expect(active.value, isTrue);
    expect(inactive.value, isFalse);
  });

  testWidgets('toggling a school off calls through and updates the row', (
    tester,
  ) async {
    _bigWindow(tester);
    final api = _FakeSchools(schools: [_school('Sint-Jan', isActive: true)]);

    await tester.pumpWidget(_host(api));
    await tester.pumpAndSettle();

    await tester.tap(find.byKey(const Key('school-toggle-Sint-Jan')));
    await tester.pumpAndSettle();

    expect(api.calls, [('Sint-Jan', false)]);
    final toggle = tester.widget<Switch>(
      find.byKey(const Key('school-toggle-Sint-Jan')),
    );
    expect(toggle.value, isFalse);
    expect(find.text('Inactive · No lesson periods'), findsOneWidget);
  });

  testWidgets('empty list explains where schools come from', (tester) async {
    _bigWindow(tester);
    final api = _FakeSchools(schools: const []);

    await tester.pumpWidget(_host(api));
    await tester.pumpAndSettle();

    expect(find.byKey(const Key('manage-schools-empty')), findsOneWidget);
  });

  testWidgets('a failed toggle surfaces an inline error', (tester) async {
    _bigWindow(tester);
    final api = _FakeSchools(
      schools: [_school('Sint-Jan', isActive: true)],
      setActiveThrows: ApiException(500, 'boom'),
    );

    await tester.pumpWidget(_host(api));
    await tester.pumpAndSettle();

    await tester.tap(find.byKey(const Key('school-toggle-Sint-Jan')));
    await tester.pumpAndSettle();

    expect(find.byKey(const Key('manage-schools-error')), findsOneWidget);
    // A sentence naming the school, never the raw exception (#383).
    expect(
      find.text('Could not update Sint-Jan. Please try again.'),
      findsOneWidget,
    );
    expect(find.textContaining('ApiException'), findsNothing);
    expect(find.textContaining('boom'), findsNothing);
    // The switch stays on — the change didn't persist.
    final toggle = tester.widget<Switch>(
      find.byKey(const Key('school-toggle-Sint-Jan')),
    );
    expect(toggle.value, isTrue);
  });

  testWidgets('each school says how many lesson periods it has (#447)', (
    tester,
  ) async {
    _bigWindow(tester);
    final api = _FakeSchools(
      schools: [
        _school(
          'Sint-Jan',
          lessonPeriods: const [
            LessonPeriod(start: '08:25', end: '09:15'),
            LessonPeriod(start: '09:15', end: '10:05'),
          ],
        ),
        _school('Sint-Maria', isActive: false),
      ],
    );

    await tester.pumpWidget(_host(api));
    await tester.pumpAndSettle();

    expect(find.text('Active · 2 lesson periods'), findsOneWidget);
    expect(find.text('Inactive · No lesson periods'), findsOneWidget);
    expect(find.byKey(const Key('school-periods-Sint-Jan')), findsOneWidget);
  });

  testWidgets('the lesson-period editor saves the timetable in start order '
      '(#447)', (tester) async {
    _bigWindow(tester);
    final api = _FakeSchools(schools: [_school('Sint-Jan')]);
    await tester.pumpWidget(_host(api));
    await tester.pumpAndSettle();

    await tester.tap(find.byKey(const Key('school-periods-Sint-Jan')));
    await tester.pumpAndSettle();
    expect(find.text('Lesson periods · Sint-Jan'), findsOneWidget);
    expect(find.byKey(const Key('lesson-periods-empty')), findsOneWidget);
    expect(_saveEnabled(tester), isTrue); // an empty timetable is a choice

    await tester.tap(find.byKey(const Key('lesson-period-add')));
    await tester.pumpAndSettle();
    expect(_saveEnabled(tester), isFalse); // a period without times
    await tester.enterText(
      find.byKey(const Key('lesson-period-start-0')),
      '10:20',
    );
    await tester.enterText(
      find.byKey(const Key('lesson-period-end-0')),
      '11:10',
    );
    await tester.tap(find.byKey(const Key('lesson-period-add')));
    await tester.pumpAndSettle();
    // A new period starts where the last one ended.
    expect(
      tester
          .widget<TextField>(find.byKey(const Key('lesson-period-start-1')))
          .controller!
          .text,
      '11:10',
    );
    await tester.enterText(
      find.byKey(const Key('lesson-period-start-1')),
      '8:25',
    );
    await tester.enterText(
      find.byKey(const Key('lesson-period-end-1')),
      '09:15',
    );
    await tester.pumpAndSettle();

    await tester.tap(find.byKey(const Key('lesson-periods-save')));
    await tester.pumpAndSettle();

    expect(api.lessonPeriodCalls, ['Sint-Jan: 08:25-09:15, 10:20-11:10']);
    expect(find.text('Lesson periods · Sint-Jan'), findsNothing);
    expect(find.text('Active · 2 lesson periods'), findsOneWidget);
  });

  testWidgets('the editor says what is wrong with a period and saves only a '
      'valid timetable (#447)', (tester) async {
    _bigWindow(tester);
    final api = _FakeSchools(
      schools: [
        _school(
          'Sint-Jan',
          lessonPeriods: const [LessonPeriod(start: '08:25', end: '09:15')],
        ),
      ],
    );
    await tester.pumpWidget(_host(api));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('school-periods-Sint-Jan')));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('lesson-period-add')));
    await tester.pumpAndSettle();

    final start = find.byKey(const Key('lesson-period-start-1'));
    final end = find.byKey(const Key('lesson-period-end-1'));
    await tester.enterText(start, '9.15');
    await tester.enterText(end, '10:05');
    await tester.pump();
    expect(find.text('Use a time like 08:25.'), findsOneWidget);
    expect(_saveEnabled(tester), isFalse);

    await tester.enterText(start, '10:05');
    await tester.enterText(end, '09:15');
    await tester.pump();
    expect(find.text('A period has to end after it starts.'), findsOneWidget);
    expect(_saveEnabled(tester), isFalse);

    await tester.enterText(start, '09:00');
    await tester.enterText(end, '09:50');
    await tester.pump();
    expect(find.text('This period overlaps another one.'), findsOneWidget);
    expect(_saveEnabled(tester), isFalse);

    // Back to back is fine.
    await tester.enterText(start, '09:15');
    await tester.pump();
    expect(find.textContaining('overlaps'), findsNothing);
    expect(_saveEnabled(tester), isTrue);

    // Removing the first period leaves the second.
    await tester.tap(find.byKey(const Key('lesson-period-remove-0')));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('lesson-periods-save')));
    await tester.pumpAndSettle();

    expect(api.lessonPeriodCalls, ['Sint-Jan: 09:15-09:50']);
  });

  testWidgets('a failed save keeps the editor open and reads as a sentence '
      '(#447)', (tester) async {
    _bigWindow(tester);
    final api = _FakeSchools(
      schools: [_school('Sint-Jan')],
      setLessonPeriodsThrows: ApiException(500, 'boom'),
    );
    await tester.pumpWidget(_host(api));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('school-periods-Sint-Jan')));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('lesson-period-add')));
    await tester.pumpAndSettle();
    await tester.enterText(
      find.byKey(const Key('lesson-period-start-0')),
      '08:25',
    );
    await tester.enterText(
      find.byKey(const Key('lesson-period-end-0')),
      '09:15',
    );
    await tester.pump();

    await tester.tap(find.byKey(const Key('lesson-periods-save')));
    await tester.pumpAndSettle();

    expect(
      find.text(
        'Could not save the lesson periods of Sint-Jan. Please try again.',
      ),
      findsOneWidget,
    );
    expect(find.text('Lesson periods · Sint-Jan'), findsOneWidget);
    expect(find.textContaining('boom'), findsNothing);
    expect(find.text('Active · No lesson periods'), findsOneWidget);
  });

  testWidgets('a failed load reads as a sentence (#383)', (tester) async {
    _bigWindow(tester);
    final api = _FakeSchools(
      schools: const [],
      listThrows: ApiException(500, 'boom'),
    );

    await tester.pumpWidget(_host(api));
    await tester.pumpAndSettle();

    expect(
      find.text('Could not load schools. Please try again.'),
      findsOneWidget,
    );
    expect(find.textContaining('ApiException'), findsNothing);
    expect(find.textContaining('boom'), findsNothing);
  });
}

bool _saveEnabled(WidgetTester tester) =>
    tester
        .widget<ElevatedButton>(find.byKey(const Key('lesson-periods-save')))
        .onPressed !=
    null;
