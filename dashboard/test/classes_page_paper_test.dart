import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/classes_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/pages/add_student_search.dart';
import 'package:anchor_dashboard/pages/classes_page.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:plink_design_system/plink_design_system.dart';

// Paper redesign of the classes / roster page (AD6, #171). Verifies the brand
// contracts the redesign introduced:
//  - the roster renders members as hairline rows (no Material DataTable) with
//    the role shown as a mono spec chip (PlinkBadge upper-cases its text);
//  - the page is paper, separated by hairlines (no Card shadow);
//  - magenta stays the single spark — only the Save (scope) commit is an
//    ElevatedButton, while "New class", "Import CSV" and "Populate from Graph"
//    stay calm ink.

ApiClient _dummyClient() => ApiClient(
  baseUrl: Uri.parse('http://localhost'),
  tokenProvider: () async => null,
);

class _FakeSessions extends SessionsApi {
  _FakeSessions(this._classes) : super(_dummyClient());
  final List<ClassSummary> _classes;

  @override
  Future<List<ClassSummary>> classes() async => _classes;
}

class _FakeClasses extends ClassesApi {
  _FakeClasses(this._roster, {List<String>? schools})
    : _schools = schools ?? const <String>[],
      super(_dummyClient());

  final ClassMembersResponse _roster;
  final List<String> _schools;

  @override
  Future<ClassMembersResponse> members(String classId) async => _roster;

  @override
  Future<List<String>> schools() async => _schools;
}

ClassMember _member(String name, String role, String id) => ClassMember(
  userId: id,
  entraOid: '00000000-0000-0000-0000-0000000000$id',
  displayName: name,
  userRole: role,
  membershipRole: '0',
  joinedAt: DateTime.utc(2025, 9, 1),
);

void main() {
  Widget host(SessionsApi sessions, ClassesApi classes) => MaterialApp(
    theme: PlinkTheme.paper,
    home: ClassesPage(sessions: sessions, classes: classes),
  );

  testWidgets(
    'roster renders as hairline rows with the role as a mono spec chip — '
    'no Material DataTable or Card',
    (tester) async {
      tester.view.physicalSize = const Size(1400, 1000);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);

      final klass = ClassSummary(
        id: 'c1',
        name: '3A',
        schoolYear: '2025-2026',
        schoolTag: 'SSM',
        classCode: '3A',
      );
      final roster = ClassMembersResponse(
        id: 'c1',
        name: '3A',
        schoolYear: '2025-2026',
        schoolTag: 'SSM',
        classCode: '3A',
        members: [
          _member('Alice Adams', 'Student', '1'),
          _member('Bob Brown', 'Teacher', '2'),
        ],
      );

      await tester.pumpWidget(
        host(_FakeSessions([klass]), _FakeClasses(roster)),
      );
      await tester.pumpAndSettle();

      // The page is paper, separated by hairlines — never a Material table/card.
      expect(find.byType(DataTable), findsNothing);
      expect(find.byType(Card), findsNothing);

      // Role reads as a mono spec chip (PlinkBadge upper-cases).
      expect(find.widgetWithText(PlinkBadge, 'STUDENT'), findsOneWidget);
      expect(find.widgetWithText(PlinkBadge, 'TEACHER'), findsOneWidget);

      // Members still read by name.
      expect(find.text('Alice Adams'), findsOneWidget);
      expect(find.text('Bob Brown'), findsOneWidget);

      expect(tester.takeException(), isNull);
    },
  );

  testWidgets(
    'the page yields exactly one magenta spark — the Save (scope) commit; '
    'New class / Import / Populate stay calm ink',
    (tester) async {
      tester.view.physicalSize = const Size(1600, 1000);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);

      final klass = ClassSummary(
        id: 'c1',
        name: '3A',
        schoolYear: '2025-2026',
        schoolTag: 'SSM',
        classCode: '3A',
      );
      final roster = ClassMembersResponse(
        id: 'c1',
        name: '3A',
        schoolYear: '2025-2026',
        schoolTag: 'SSM',
        classCode: '3A',
        members: const [],
      );

      await tester.pumpWidget(
        host(
          _FakeSessions([klass]),
          _FakeClasses(roster, schools: const ['SSM', 'SJI']),
        ),
      );
      await tester.pumpAndSettle();

      // The scope Save is the single ElevatedButton on the page (the one
      // spark). "New class", "Import CSV", "Populate from Graph", "Delete class"
      // are all calm ink (OutlinedButton).
      final save = find.byKey(const Key('classes-save-codes-button'));
      expect(save, findsOneWidget);
      expect(find.byType(ElevatedButton), findsOneWidget);

      expect(find.widgetWithText(OutlinedButton, 'New class'), findsOneWidget);
      expect(find.widgetWithText(OutlinedButton, 'Import CSV'), findsOneWidget);
      expect(
        find.widgetWithText(OutlinedButton, 'Populate from Graph'),
        findsOneWidget,
      );
      expect(
        find.widgetWithText(OutlinedButton, 'Delete class'),
        findsOneWidget,
      );

      expect(tester.takeException(), isNull);
    },
  );

  testWidgets(
    'the add-student typeahead renders matches in a hairline panel, not a Card',
    (tester) async {
      tester.view.physicalSize = const Size(1600, 1000);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);

      await tester.pumpWidget(
        MaterialApp(
          theme: PlinkTheme.paper,
          home: Scaffold(
            backgroundColor: PlinkColors.paper,
            body: AddStudentSearch(
              onSearch: (q) async => [
                DirectoryUser(
                  entraOid: '00000000-0000-0000-0000-000000000001',
                  displayName: 'Alice Example',
                  upn: 'alice@example.com',
                ),
              ],
              onAdd: (_, _) async {},
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();

      await tester.enterText(find.byType(TextField), 'al');
      await tester.pump(const Duration(milliseconds: 300));
      await tester.pumpAndSettle();

      // Result rendered, but the panel is a hairline-bounded container, not a
      // raised Material Card (and no Material ListTile).
      expect(find.text('Alice Example'), findsOneWidget);
      expect(find.text('alice@example.com'), findsOneWidget);
      expect(find.byType(Card), findsNothing);
      expect(find.byType(ListTile), findsNothing);

      expect(tester.takeException(), isNull);
    },
  );
}
