import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/classes_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/pages/classes_page.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

ApiClient _dummyClient() =>
    ApiClient(baseUrl: Uri.parse('http://localhost'), tokenProvider: () async => null);

class _FakeSessions extends SessionsApi {
  _FakeSessions(this._classes) : super(_dummyClient());
  final List<ClassSummary> _classes;

  @override
  Future<List<ClassSummary>> classes() async => _classes;
}

class _FakeClasses extends ClassesApi {
  _FakeClasses(this._roster, {List<String>? schools, this.onBulkImport})
    : _schools = schools ?? const <String>[],
      super(_dummyClient());

  final ClassMembersResponse _roster;
  final List<String> _schools;
  final Future<List<ClassMembershipImportResult>> Function(String classId)? onBulkImport;
  int updateCodesCalls = 0;
  int bulkImportCalls = 0;

  @override
  Future<ClassMembersResponse> members(String classId) async => _roster;

  @override
  Future<List<String>> schools() async => _schools;

  @override
  Future<ClassSummary> updateCodes(
    String classId, {
    required String? schoolTag,
    required String? classCode,
  }) async {
    updateCodesCalls++;
    return ClassSummary(
      id: classId,
      name: _roster.name,
      schoolYear: _roster.schoolYear,
      schoolTag: schoolTag,
      classCode: classCode,
    );
  }

  @override
  Future<List<ClassMembershipImportResult>> bulkImportFromDirectory(
    String classId,
  ) async {
    bulkImportCalls++;
    return onBulkImport?.call(classId) ?? const <ClassMembershipImportResult>[];
  }
}

void main() {
  testWidgets('roster shows UserRole as a string and has no Joined column', (
    tester,
  ) async {
    tester.view.physicalSize = const Size(1400, 1000);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);

    final klass = ClassSummary(id: 'c1', name: '3A', schoolYear: '2025-2026');
    final roster = ClassMembersResponse(
      id: 'c1',
      name: '3A',
      schoolYear: '2025-2026',
      members: [
        ClassMember(
          userId: 'u1',
          entraOid: '00000000-0000-0000-0000-000000000001',
          displayName: 'Alice Adams',
          userRole: 'Student',
          membershipRole: '0',
          joinedAt: DateTime.utc(2025, 9, 1),
        ),
        ClassMember(
          userId: 'u2',
          entraOid: '00000000-0000-0000-0000-000000000002',
          displayName: 'Bob Brown',
          userRole: 'Teacher',
          membershipRole: '1',
          joinedAt: DateTime.utc(2025, 9, 2),
        ),
      ],
    );

    await tester.pumpWidget(
      MaterialApp(
        home: ClassesPage(
          sessions: _FakeSessions([klass]),
          classes: _FakeClasses(roster),
        ),
      ),
    );
    await tester.pumpAndSettle();

    // Column headers: Role kept, Joined dropped.
    expect(find.text('Display name'), findsOneWidget);
    expect(find.text('Role'), findsOneWidget);
    expect(find.text('Joined'), findsNothing);

    // Role cells render the UserRole string, not the numeric membershipRole.
    expect(find.text('Student'), findsOneWidget);
    expect(find.text('Teacher'), findsOneWidget);
    expect(find.text('0'), findsNothing);
    expect(find.text('1'), findsNothing);

    // The dropped Joined column no longer renders a date.
    expect(find.text('2025-09-01'), findsNothing);
  });

  testWidgets(
    'scope row: empty schoolTag/classCode disables search + import + populate',
    (tester) async {
      tester.view.physicalSize = const Size(1600, 1000);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);

      // schoolTag / classCode unset on the class.
      final klass = ClassSummary(id: 'c1', name: '3A', schoolYear: '2025-2026');
      final roster = ClassMembersResponse(
        id: 'c1',
        name: '3A',
        schoolYear: '2025-2026',
        members: const [],
      );

      await tester.pumpWidget(
        MaterialApp(
          home: ClassesPage(
            sessions: _FakeSessions([klass]),
            classes: _FakeClasses(roster, schools: const ['SSM', 'SJI']),
          ),
        ),
      );
      await tester.pumpAndSettle();

      // Scope row visible.
      expect(find.text('School'), findsOneWidget);
      expect(find.text('Class code'), findsOneWidget);
      expect(find.text('Save'), findsOneWidget);

      // Action buttons are present but disabled until scope is set.
      Finder buttonByLabel(String label) => find.ancestor(
        of: find.text(label),
        matching: find.byWidgetPredicate((w) => w is ButtonStyleButton),
      );
      final importBtn = tester.widget<ButtonStyleButton>(
        buttonByLabel('Import CSV'),
      );
      final populateBtn = tester.widget<ButtonStyleButton>(
        buttonByLabel('Populate from Graph'),
      );
      expect(importBtn.onPressed, isNull);
      expect(populateBtn.onPressed, isNull);
    },
  );

  testWidgets(
    'scope row: setting school + code becomes dirty and Save calls updateCodes',
    (tester) async {
      tester.view.physicalSize = const Size(1600, 1000);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);

      final klass = ClassSummary(id: 'c1', name: '3A', schoolYear: '2025-2026');
      final roster = ClassMembersResponse(
        id: 'c1',
        name: '3A',
        schoolYear: '2025-2026',
        members: const [],
      );

      final fakeClasses = _FakeClasses(roster, schools: const ['SSM', 'SJI']);

      await tester.pumpWidget(
        MaterialApp(
          home: ClassesPage(
            sessions: _FakeSessions([klass]),
            classes: fakeClasses,
          ),
        ),
      );
      await tester.pumpAndSettle();

      // Save is disabled while clean.
      Finder saveBtn() => find.ancestor(
        of: find.text('Save'),
        matching: find.byWidgetPredicate((w) => w is ButtonStyleButton),
      );
      expect(tester.widget<ButtonStyleButton>(saveBtn()).onPressed, isNull);

      // Pick a school and type a class code.
      await tester.tap(find.byType(DropdownButtonFormField<String?>));
      await tester.pumpAndSettle();
      await tester.tap(find.text('SSM').last);
      await tester.pumpAndSettle();
      await tester.enterText(find.byType(TextField).first, '3A');
      await tester.pump();

      // Now dirty — Save enabled.
      expect(
        tester.widget<ButtonStyleButton>(saveBtn()).onPressed,
        isNotNull,
      );

      await tester.tap(saveBtn());
      await tester.pumpAndSettle();

      expect(fakeClasses.updateCodesCalls, 1);
    },
  );

  testWidgets(
    'Populate from Graph enabled once scope is saved and calls bulkImportFromDirectory',
    (tester) async {
      tester.view.physicalSize = const Size(1600, 1000);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);

      // Class already bound to a school + code.
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

      final fakeClasses = _FakeClasses(
        roster,
        schools: const ['SSM', 'SJI'],
        onBulkImport: (_) async => const [],
      );

      await tester.pumpWidget(
        MaterialApp(
          home: ClassesPage(
            sessions: _FakeSessions([klass]),
            classes: fakeClasses,
          ),
        ),
      );
      await tester.pumpAndSettle();

      Finder populateBtn() => find.ancestor(
        of: find.text('Populate from Graph'),
        matching: find.byWidgetPredicate((w) => w is ButtonStyleButton),
      );
      expect(
        tester.widget<ButtonStyleButton>(populateBtn()).onPressed,
        isNotNull,
      );

      await tester.tap(populateBtn());
      await tester.pumpAndSettle();

      expect(fakeClasses.bulkImportCalls, 1);
    },
  );
}
