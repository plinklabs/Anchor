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
  _FakeClasses(this._roster) : super(_dummyClient());
  final ClassMembersResponse _roster;

  @override
  Future<ClassMembersResponse> members(String classId) async => _roster;
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
}
