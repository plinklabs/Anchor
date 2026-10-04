import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/auth_token_store.dart';
import 'package:anchor_dashboard/api/bundles_api.dart';
import 'package:anchor_dashboard/api/classes_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/auth/msal_auth_service.dart';
import 'package:anchor_dashboard/main.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'support/e2e_binding.dart';

// Real-app e2e for archiving classes and deleting a class with its sessions
// (#395). A school year's classes used to stay forever: a class with sessions
// couldn't be deleted, so the Classes page and Home's class picker grew every
// September. This boots the real AnchorDashboard (real fonts, router, window,
// and the app's own navigation between Home and Classes) against fake APIs
// that keep one class store the way the backend does: GET classes leaves
// archived classes out unless asked, archive/restore flip them, and a delete
// removes them. It walks a teacher archiving last year's class, finding and
// restoring an archived one, and deleting a class with its sessions through
// the typed-name dialog, then checks Home's picker offers exactly the classes
// still in use. A second case runs the Classes page in Dutch at the narrowest
// window the dashboard is tested at, where the longer Dutch labels must fit.

ApiClient _dummyClient() => ApiClient(
  baseUrl: Uri.parse('http://localhost'),
  tokenProvider: () async => null,
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

ClassSummary _withArchived(ClassSummary c, bool isArchived) => ClassSummary(
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
  Future<MeResponse> me() async =>
      MeResponse(id: 't1', displayName: 'Teacher', role: 'Teacher');

  /// As the backend: archived classes left out.
  @override
  Future<List<ClassSummary>> classes() async =>
      _store.where((c) => !c.isArchived).toList();

  @override
  Future<List<ActiveSession>> activeSessions() async => const [];
}

class _FakeClasses extends ClassesApi {
  _FakeClasses(this._store) : super(_dummyClient());

  final List<ClassSummary> _store;

  /// Each delete, as (class id, includeSessions).
  final deletes = <(String, bool)>[];

  @override
  Future<ClassMembersResponse> members(String classId) async {
    final klass = _store.firstWhere((c) => c.id == classId);
    return ClassMembersResponse(
      id: klass.id,
      name: klass.name,
      schoolYear: klass.schoolYear,
      members: const [],
    );
  }

  @override
  Future<List<String>> schools() async => const ['SSM'];

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
    return _store[i] = _withArchived(_store[i], isArchived);
  }

  @override
  Future<void> deleteClass(
    String classId, {
    bool includeSessions = false,
  }) async {
    deletes.add((classId, includeSessions));
    _store.removeWhere((c) => c.id == classId);
  }
}

AuthTokenStore _signedIn() => AuthTokenStore()
  ..setSession(
    token: 'fake-token',
    account: const AccountInfo(
      homeAccountId: 'home-1',
      username: 'teacher@school.example',
      displayName: 'Teacher',
      department: null,
    ),
  );

Future<void> _boot(
  WidgetTester tester,
  List<ClassSummary> store,
  _FakeClasses classes,
) async {
  await tester.pumpWidget(
    AnchorDashboard(
      tokens: _signedIn(),
      auth: _FakeAuth(),
      api: _dummyClient(),
      sessions: _FakeSessions(store),
      bundles: BundlesApi(_dummyClient()),
      classes: classes,
      apiBaseUrl: Uri.parse('http://localhost'),
    ),
  );
  await tester.pumpAndSettle();
}

/// The "name (year)" entries Home's class picker offers, read off its open
/// menu, which is then closed again.
Future<Set<String>> _homePickerEntries(WidgetTester tester) async {
  final picker = find.byKey(const Key('class-picker'));
  expect(picker, findsOneWidget);
  await tester.tap(picker);
  await tester.pumpAndSettle();
  final entries = <String>{
    for (final item in tester.widgetList<DropdownMenuItem<ClassSummary>>(
      find.byType(DropdownMenuItem<ClassSummary>),
    ))
      '${item.value!.name} (${item.value!.schoolYear})',
  };
  // Close the menu without changing the pick.
  await tester.tapAt(const Offset(5, 5));
  await tester.pumpAndSettle();
  return entries;
}

void main() {
  ensureE2eBinding();

  testWidgets(
    'a teacher archives a class, restores another, deletes one with its '
    "sessions, and Home's picker follows (#395)",
    (tester) async {
      final store = <ClassSummary>[
        ClassSummary(id: 'c1', name: '3A', schoolYear: '2025-2026'),
        ClassSummary(
          id: 'c2',
          name: '6EWI',
          schoolYear: '2025-2026',
          sessionCount: 4,
        ),
        ClassSummary(
          id: 'c0',
          name: 'Old',
          schoolYear: '2024-2025',
          isArchived: true,
          sessionCount: 2,
        ),
      ];
      final classes = _FakeClasses(store);
      await _boot(tester, store, classes);

      // Home's picker never offers the archived class.
      expect(await _homePickerEntries(tester), {
        '3A (2025-2026)',
        '6EWI (2025-2026)',
      });

      await tester.tap(find.byKey(const Key('nav-classes')));
      await tester.pumpAndSettle();
      expect(find.text('3A (2025-2026)'), findsOneWidget);
      expect(find.text('Old'), findsNothing);

      // Archive 3A, through its confirm dialog: it leaves the list.
      await tester.tap(find.byKey(const Key('classes-archive-button')));
      await tester.pumpAndSettle();
      expect(
        find.widgetWithText(AlertDialog, 'Archive class?'),
        findsOneWidget,
      );
      await tester.tap(find.byKey(const Key('classes-archive-confirm-button')));
      await tester.pumpAndSettle();
      expect(find.text('3A'), findsNothing);
      expect(find.text('6EWI (2025-2026)'), findsOneWidget);

      // The Archived switch brings archived classes back into view, badged.
      await tester.tap(find.byKey(const Key('classes-show-archived')));
      await tester.pumpAndSettle();
      expect(find.text('3A'), findsOneWidget);
      expect(find.text('Old'), findsOneWidget);
      expect(find.text('ARCHIVED'), findsNWidgets(2));

      // Restore Old.
      await tester.tap(find.text('Old'));
      await tester.pumpAndSettle();
      expect(find.text('Old (2024-2025)'), findsOneWidget);
      await tester.tap(find.byKey(const Key('classes-restore-button')));
      await tester.pumpAndSettle();
      expect(find.text('ARCHIVED'), findsOneWidget); // 3A's row only
      expect(find.byKey(const Key('classes-archive-button')), findsOneWidget);

      // Delete 6EWI with its sessions: the dialog counts them, and Delete
      // waits for the class's name.
      await tester.tap(find.text('6EWI'));
      await tester.pumpAndSettle();
      await tester.tap(find.widgetWithText(OutlinedButton, 'Delete class'));
      await tester.pumpAndSettle();
      expect(
        find.textContaining('6EWI (2025-2026) has 4 sessions.'),
        findsOneWidget,
      );
      expect(find.textContaining("students' activity data"), findsOneWidget);
      final delete = find.byKey(const Key('classes-delete-confirm-button'));
      expect(tester.widget<FilledButton>(delete).onPressed, isNull);
      await tester.enterText(
        find.byKey(const Key('classes-delete-type-name')),
        '6EWI',
      );
      await tester.pump();
      expect(tester.widget<FilledButton>(delete).onPressed, isNotNull);
      await tester.tap(delete);
      await tester.pumpAndSettle();

      expect(classes.deletes, [('c2', true)]);
      expect(find.text('6EWI'), findsNothing);

      // Home's picker: the restored class is back, the archived and the
      // deleted ones are not.
      await tester.tap(find.byKey(const Key('nav-home')));
      await tester.pumpAndSettle();
      expect(await _homePickerEntries(tester), {'Old (2024-2025)'});

      // No overflow or other exception anywhere along the way.
      expect(tester.takeException(), isNull);
    },
  );

  testWidgets(
    'the Classes page and its archive and delete dialogs fit in Dutch in a '
    'narrow window (#395)',
    (tester) async {
      tester.platformDispatcher.localesTestValue = const <Locale>[
        Locale('nl', 'BE'),
      ];
      addTearDown(
        () => tester.platformDispatcher.localesTestValue = const <Locale>[
          defaultE2eLocale,
        ],
      );
      tester.view.physicalSize = const Size(750, 900);

      final store = <ClassSummary>[
        ClassSummary(
          id: 'c2',
          name: '6EWI',
          schoolYear: '2025-2026',
          sessionCount: 4,
        ),
      ];
      await _boot(tester, store, _FakeClasses(store));
      await tester.tap(find.byKey(const Key('nav-classes')));
      await tester.pumpAndSettle();

      expect(find.text('Gearchiveerd'), findsOneWidget);
      expect(find.widgetWithText(OutlinedButton, 'Archiveren'), findsOneWidget);
      expect(
        find.widgetWithText(OutlinedButton, 'Klas verwijderen'),
        findsOneWidget,
      );

      await tester.tap(find.byKey(const Key('classes-archive-button')));
      await tester.pumpAndSettle();
      expect(
        find.widgetWithText(AlertDialog, 'Klas archiveren?'),
        findsOneWidget,
      );
      await tester.tap(find.widgetWithText(TextButton, 'Annuleren'));
      await tester.pumpAndSettle();

      await tester.tap(find.widgetWithText(OutlinedButton, 'Klas verwijderen'));
      await tester.pumpAndSettle();
      expect(find.textContaining('heeft 4 sessies'), findsOneWidget);
      expect(
        find.widgetWithText(FilledButton, 'Klas en sessies verwijderen'),
        findsOneWidget,
      );
      expect(
        find.byKey(const Key('classes-delete-archive-instead')),
        findsOneWidget,
      );
      await tester.tap(find.widgetWithText(TextButton, 'Annuleren'));
      await tester.pumpAndSettle();

      expect(tester.takeException(), isNull);
    },
  );
}
