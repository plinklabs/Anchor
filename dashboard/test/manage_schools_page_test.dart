import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/schools_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart' show ApiException;
import 'package:anchor_dashboard/pages/manage_schools_page.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

// Widget tests for the "Schools" admin sub-tab (#301): they drive the page
// against a scripted fake SchoolsApi to prove the list renders with each
// school's active state, toggling a switch calls through and updates the row,
// and an API failure surfaces an inline error. Layout/font/composition is
// covered by the integration test.

ApiClient _dummyClient() => ApiClient(
  baseUrl: Uri.parse('http://localhost'),
  tokenProvider: () async => null,
);

School _school(String name, {bool isActive = true}) =>
    School(name: name, isActive: isActive);

class _FakeSchools extends SchoolsApi {
  _FakeSchools({required this.schools, this.setActiveThrows})
    : super(_dummyClient());

  List<School> schools;
  Object? setActiveThrows;

  final List<(String, bool)> calls = [];

  @override
  Future<List<School>> listSchools() async => schools;

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

Widget _host(SchoolsApi schools) =>
    MaterialApp(home: ManageSchoolsPage(schools: schools));

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
    expect(find.text('Inactive'), findsOneWidget);
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
    // The switch stays on — the change didn't persist.
    final toggle = tester.widget<Switch>(
      find.byKey(const Key('school-toggle-Sint-Jan')),
    );
    expect(toggle.value, isTrue);
  });
}
