import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/classes_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/l10n/app_localizations.dart';
import 'package:anchor_dashboard/pages/classes_page.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

// #395: archiving a class, restoring it, and deleting a class together with its
// sessions, on the Classes page. The fakes keep one class store and treat it as
// the backend does: SessionsApi.classes leaves archived classes out, and
// listIncludingArchived doesn't.

ApiClient _dummyClient() => ApiClient(
  baseUrl: Uri.parse('http://localhost'),
  tokenProvider: () async => null,
);

ClassSummary _class(
  String id,
  String name, {
  String schoolYear = '2025-2026',
  bool isArchived = false,
  int sessionCount = 0,
}) => ClassSummary(
  id: id,
  name: name,
  schoolYear: schoolYear,
  isArchived: isArchived,
  sessionCount: sessionCount,
);

ClassSummary _archived(ClassSummary c, bool isArchived) => ClassSummary(
  id: c.id,
  name: c.name,
  schoolYear: c.schoolYear,
  schoolTag: c.schoolTag,
  classCode: c.classCode,
  isArchived: isArchived,
  sessionCount: c.sessionCount,
);

class _FakeSessions extends SessionsApi {
  _FakeSessions(this._store) : super(_dummyClient());
  final List<ClassSummary> _store;

  @override
  Future<List<ClassSummary>> classes() async =>
      _store.where((c) => !c.isArchived).toList();
}

class _FakeClasses extends ClassesApi {
  _FakeClasses(this._store) : super(_dummyClient());
  final List<ClassSummary> _store;

  /// Each delete, as (class id, includeSessions).
  final deletes = <(String, bool)>[];
  Object? deleteError;
  Object? createError;

  @override
  Future<ClassMembersResponse> members(String classId) async {
    final c = _store.firstWhere((c) => c.id == classId);
    return ClassMembersResponse(
      id: c.id,
      name: c.name,
      schoolYear: c.schoolYear,
      members: const [],
    );
  }

  @override
  Future<List<String>> schools() async => const [];

  @override
  Future<List<ClassSummary>> listIncludingArchived() async => List.of(_store);

  @override
  Future<ClassSummary> archiveClass(String classId) async =>
      _setArchived(classId, true);

  @override
  Future<ClassSummary> unarchiveClass(String classId) async =>
      _setArchived(classId, false);

  ClassSummary _setArchived(String classId, bool isArchived) {
    final i = _store.indexWhere((c) => c.id == classId);
    return _store[i] = _archived(_store[i], isArchived);
  }

  @override
  Future<void> deleteClass(
    String classId, {
    bool includeSessions = false,
  }) async {
    deletes.add((classId, includeSessions));
    if (deleteError case final error?) throw error;
    _store.removeWhere((c) => c.id == classId);
  }

  @override
  Future<ClassSummary> createClass({
    required String name,
    required String schoolYear,
    String? schoolTag,
    String? classCode,
  }) async {
    if (createError case final error?) throw error;
    final created = _class('new-$name', name, schoolYear: schoolYear);
    _store.add(created);
    return created;
  }
}

Future<void> _pump(
  WidgetTester tester,
  List<ClassSummary> store,
  _FakeClasses classes, {
  Locale locale = const Locale('en'),
}) async {
  tester.view.physicalSize = const Size(1600, 1000);
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.resetPhysicalSize);
  addTearDown(tester.view.resetDevicePixelRatio);
  await tester.pumpWidget(
    MaterialApp(
      locale: locale,
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      home: ClassesPage(sessions: _FakeSessions(store), classes: classes),
    ),
  );
  await tester.pumpAndSettle();
}

/// The class rows in the list, by name: the text that is both a row's name
/// and not the roster pane's "name (year)" header.
Finder _row(String name) => find.text(name);

FilledButton _deleteButton(WidgetTester tester) => tester.widget<FilledButton>(
  find.byKey(const Key('classes-delete-confirm-button')),
);

void main() {
  testWidgets('Archive confirms, then the class leaves the list (#395)', (
    tester,
  ) async {
    final store = [_class('c1', '3A'), _class('c2', '4B')];
    final classes = _FakeClasses(store);
    await _pump(tester, store, classes);
    expect(find.text('3A (2025-2026)'), findsOneWidget);

    await tester.tap(find.byKey(const Key('classes-archive-button')));
    await tester.pumpAndSettle();
    expect(find.widgetWithText(AlertDialog, 'Archive class?'), findsOneWidget);
    expect(find.textContaining('class picker on Home'), findsOneWidget);
    await tester.tap(find.byKey(const Key('classes-archive-confirm-button')));
    await tester.pumpAndSettle();

    expect(store.singleWhere((c) => c.id == 'c1').isArchived, isTrue);
    // Gone from the list, as from Home's picker; the next class is selected.
    expect(_row('3A'), findsNothing);
    expect(find.text('4B (2025-2026)'), findsOneWidget);
  });

  testWidgets('cancelling the Archive dialog archives nothing', (tester) async {
    final store = [_class('c1', '3A')];
    final classes = _FakeClasses(store);
    await _pump(tester, store, classes);

    await tester.tap(find.byKey(const Key('classes-archive-button')));
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(TextButton, 'Cancel'));
    await tester.pumpAndSettle();

    expect(store.single.isArchived, isFalse);
    expect(_row('3A'), findsOneWidget);
  });

  testWidgets('the Archived switch lists archived classes, and Restore brings '
      'one back (#395)', (tester) async {
    final store = [
      _class('c1', '3A'),
      _class('c0', 'Old', schoolYear: '2024-2025', isArchived: true),
    ];
    final classes = _FakeClasses(store);
    await _pump(tester, store, classes);
    expect(_row('Old'), findsNothing);
    // The switch's label; no class wears the (upper-case) badge.
    expect(find.text('Archived'), findsOneWidget);
    expect(find.text('ARCHIVED'), findsNothing);

    await tester.tap(find.byKey(const Key('classes-show-archived')));
    await tester.pumpAndSettle();
    expect(_row('Old'), findsOneWidget);
    // The class that was selected stays selected.
    expect(find.text('3A (2025-2026)'), findsOneWidget);
    // The archived row wears the badge.
    expect(find.text('ARCHIVED'), findsOneWidget);

    await tester.tap(_row('Old'));
    await tester.pumpAndSettle();
    expect(find.text('Old (2024-2025)'), findsOneWidget);
    // The row's badge and the roster pane's; Restore in Archive's place.
    expect(find.text('ARCHIVED'), findsNWidgets(2));
    expect(find.byKey(const Key('classes-archive-button')), findsNothing);

    await tester.tap(find.byKey(const Key('classes-restore-button')));
    await tester.pumpAndSettle();
    expect(store.singleWhere((c) => c.id == 'c0').isArchived, isFalse);
    expect(find.text('ARCHIVED'), findsNothing);
    expect(find.byKey(const Key('classes-archive-button')), findsOneWidget);

    // Restored, it stays in the list with archived classes hidden again.
    await tester.tap(find.byKey(const Key('classes-show-archived')));
    await tester.pumpAndSettle();
    expect(_row('Old'), findsOneWidget);
    expect(find.text('Old (2024-2025)'), findsOneWidget);
  });

  testWidgets('archiving with archived classes shown keeps the class listed, '
      'badged', (tester) async {
    final store = [_class('c1', '3A')];
    final classes = _FakeClasses(store);
    await _pump(tester, store, classes);
    await tester.tap(find.byKey(const Key('classes-show-archived')));
    await tester.pumpAndSettle();

    await tester.tap(find.byKey(const Key('classes-archive-button')));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('classes-archive-confirm-button')));
    await tester.pumpAndSettle();

    expect(_row('3A'), findsOneWidget);
    expect(find.text('ARCHIVED'), findsNWidgets(2));
    expect(find.byKey(const Key('classes-restore-button')), findsOneWidget);
  });

  testWidgets('deleting a class without sessions is a plain confirm, and '
      'leaves sessions out of the request', (tester) async {
    final store = [_class('c1', '3A')];
    final classes = _FakeClasses(store);
    await _pump(tester, store, classes);

    await tester.tap(find.widgetWithText(OutlinedButton, 'Delete class'));
    await tester.pumpAndSettle();
    expect(
      find.text(
        'Delete 3A (2025-2026)? This removes the class and its roster.',
      ),
      findsOneWidget,
    );
    expect(find.byKey(const Key('classes-delete-type-name')), findsNothing);
    expect(
      find.byKey(const Key('classes-delete-archive-instead')),
      findsNothing,
    );
    expect(_deleteButton(tester).onPressed, isNotNull);

    await tester.tap(find.widgetWithText(FilledButton, 'Delete'));
    await tester.pumpAndSettle();

    expect(classes.deletes, [('c1', false)]);
    expect(_row('3A'), findsNothing);
  });

  testWidgets('deleting a class with sessions says how many go with it, '
      'offers Archive, and waits for the class name (#395)', (tester) async {
    final store = [_class('c1', '6EWI', sessionCount: 4), _class('c2', '3A')];
    final classes = _FakeClasses(store);
    await _pump(tester, store, classes);

    await tester.tap(find.widgetWithText(OutlinedButton, 'Delete class'));
    await tester.pumpAndSettle();
    expect(
      find.textContaining(
        "6EWI (2025-2026) has 4 sessions. Deleting the class deletes them "
        "too, and with them the students' activity data.",
      ),
      findsOneWidget,
    );
    expect(find.textContaining('Archive it instead'), findsOneWidget);
    expect(
      find.byKey(const Key('classes-delete-archive-instead')),
      findsOneWidget,
    );
    expect(
      find.widgetWithText(FilledButton, 'Delete class and sessions'),
      findsOneWidget,
    );
    expect(_deleteButton(tester).onPressed, isNull);

    await tester.enterText(
      find.byKey(const Key('classes-delete-type-name')),
      '6ewi',
    );
    await tester.pump();
    expect(_deleteButton(tester).onPressed, isNull);

    await tester.enterText(
      find.byKey(const Key('classes-delete-type-name')),
      '6EWI',
    );
    await tester.pump();
    expect(_deleteButton(tester).onPressed, isNotNull);
    await tester.tap(find.byKey(const Key('classes-delete-confirm-button')));
    await tester.pumpAndSettle();

    expect(classes.deletes, [('c1', true)]);
    expect(_row('6EWI'), findsNothing);
    expect(find.text('3A (2025-2026)'), findsOneWidget);
  });

  testWidgets('a class with one session reads in the singular', (tester) async {
    final store = [_class('c1', '6EWI', sessionCount: 1)];
    await _pump(tester, store, _FakeClasses(store));

    await tester.tap(find.widgetWithText(OutlinedButton, 'Delete class'));
    await tester.pumpAndSettle();
    expect(find.textContaining('has 1 session. Deleting'), findsOneWidget);
  });

  testWidgets('Archive in the delete dialog archives instead of deleting', (
    tester,
  ) async {
    final store = [_class('c1', '6EWI', sessionCount: 2), _class('c2', '3A')];
    final classes = _FakeClasses(store);
    await _pump(tester, store, classes);

    await tester.tap(find.widgetWithText(OutlinedButton, 'Delete class'));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('classes-delete-archive-instead')));
    await tester.pumpAndSettle();

    expect(classes.deletes, isEmpty);
    expect(store.singleWhere((c) => c.id == 'c1').isArchived, isTrue);
    expect(_row('6EWI'), findsNothing);
  });

  testWidgets('an archived class with sessions has no Archive alternative in '
      'its delete dialog', (tester) async {
    final store = [_class('c1', '6EWI', sessionCount: 2, isArchived: true)];
    final classes = _FakeClasses(store);
    await _pump(tester, store, classes);
    await tester.tap(find.byKey(const Key('classes-show-archived')));
    await tester.pumpAndSettle();

    await tester.tap(find.widgetWithText(OutlinedButton, 'Delete class'));
    await tester.pumpAndSettle();
    expect(find.textContaining('has 2 sessions'), findsOneWidget);
    expect(find.textContaining('Archive it instead'), findsNothing);
    expect(
      find.byKey(const Key('classes-delete-archive-instead')),
      findsNothing,
    );
  });

  testWidgets('a delete refused while a session runs says so and keeps the '
      'class', (tester) async {
    final store = [_class('c1', '6EWI', sessionCount: 2)];
    final classes = _FakeClasses(store)
      ..deleteError = ApiException(409, '{"error":"still running"}');
    await _pump(tester, store, classes);

    await tester.tap(find.widgetWithText(OutlinedButton, 'Delete class'));
    await tester.pumpAndSettle();
    await tester.enterText(
      find.byKey(const Key('classes-delete-type-name')),
      '6EWI',
    );
    await tester.pump();
    await tester.tap(find.byKey(const Key('classes-delete-confirm-button')));
    await tester.pumpAndSettle();

    expect(
      find.text(
        'A session of this class is still running. End it, then delete the '
        'class.',
      ),
      findsOneWidget,
    );
    expect(_row('6EWI'), findsOneWidget);
  });

  testWidgets('a plain delete refused because sessions appeared asks for a '
      'reload', (tester) async {
    final store = [_class('c1', '3A')];
    final classes = _FakeClasses(store)
      ..deleteError = ApiException(409, '{"error":"has sessions"}');
    await _pump(tester, store, classes);

    await tester.tap(find.widgetWithText(OutlinedButton, 'Delete class'));
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(FilledButton, 'Delete'));
    await tester.pumpAndSettle();

    expect(classes.deletes, [('c1', false)]);
    expect(find.textContaining('Reload the page'), findsOneWidget);
  });

  testWidgets('a new class named like an archived one says to restore it', (
    tester,
  ) async {
    final store = [_class('c1', '3A')];
    final classes = _FakeClasses(store)
      ..createError = ApiException(
        409,
        '{"error":"a class named \'6EWI\' already exists","archived":true}',
      );
    await _pump(tester, store, classes);

    await tester.tap(find.widgetWithText(OutlinedButton, 'New class'));
    await tester.pumpAndSettle();
    await tester.enterText(find.widgetWithText(TextField, 'Name'), '6EWI');
    await tester.enterText(
      find.widgetWithText(TextField, 'School year'),
      '2025-2026',
    );
    await tester.pump();
    await tester.tap(find.widgetWithText(ElevatedButton, 'Create'));
    await tester.pumpAndSettle();

    expect(
      find.text(
        '6EWI (2025-2026) already exists and is archived. Restore it '
        'instead: switch on Archived above the class list.',
      ),
      findsOneWidget,
    );

    // A clash with a class in use keeps the generic message.
    classes.createError = ApiException(
      409,
      '{"error":"a class named \'6EWI\' already exists","archived":false}',
    );
    await tester.tap(find.widgetWithText(ElevatedButton, 'Create'));
    await tester.pumpAndSettle();
    expect(find.textContaining('is archived'), findsNothing);
    expect(
      find.text('Could not create class. Please try again.'),
      findsOneWidget,
    );
  });

  testWidgets('the archive and delete copy is Dutch in Dutch (#395)', (
    tester,
  ) async {
    final store = [_class('c1', '6EWI', sessionCount: 3)];
    await _pump(tester, store, _FakeClasses(store), locale: const Locale('nl'));

    expect(find.text('Gearchiveerd'), findsOneWidget);
    expect(find.widgetWithText(OutlinedButton, 'Archiveren'), findsOneWidget);

    await tester.tap(find.widgetWithText(OutlinedButton, 'Klas verwijderen'));
    await tester.pumpAndSettle();
    expect(
      find.textContaining(
        '6EWI (2025-2026) heeft 3 sessies. Als je de klas verwijdert, '
        'verdwijnen die sessies ook',
      ),
      findsOneWidget,
    );
    expect(find.text('Typ 6EWI om te bevestigen'), findsOneWidget);
    expect(
      find.widgetWithText(FilledButton, 'Klas en sessies verwijderen'),
      findsOneWidget,
    );
    expect(
      find.descendant(
        of: find.byType(AlertDialog),
        matching: find.widgetWithText(OutlinedButton, 'Archiveren'),
      ),
      findsOneWidget,
    );
  });
}
