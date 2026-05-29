import 'package:anchor_dashboard/api/classes_api.dart';
import 'package:anchor_dashboard/pages/add_student_search.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

DirectoryUser _user(String name, {String? upn}) => DirectoryUser(
  entraOid: '00000000-0000-0000-0000-000000000001',
  displayName: name,
  upn: upn,
);

Future<void> _pumpSearch(
  WidgetTester tester, {
  required Future<List<DirectoryUser>> Function(String) onSearch,
  required Future<void> Function(String, String?) onAdd,
}) async {
  await tester.pumpWidget(
    MaterialApp(
      home: Scaffold(
        body: AddStudentSearch(onSearch: onSearch, onAdd: onAdd),
      ),
    ),
  );
}

void main() {
  group('AddStudentSearch', () {
    testWidgets('does not search until the minimum query length is reached', (
      tester,
    ) async {
      final queries = <String>[];
      await _pumpSearch(
        tester,
        onSearch: (q) async {
          queries.add(q);
          return const <DirectoryUser>[];
        },
        onAdd: (_, _) async {},
      );

      await tester.enterText(find.byType(TextField), 'a');
      await tester.pump(const Duration(milliseconds: 400));

      expect(queries, isEmpty);
    });

    testWidgets('debounces and shows results after the query settles', (
      tester,
    ) async {
      final queries = <String>[];
      await _pumpSearch(
        tester,
        onSearch: (q) async {
          queries.add(q);
          return [_user('Alice Example', upn: 'alice@example.com')];
        },
        onAdd: (_, _) async {},
      );

      await tester.enterText(find.byType(TextField), 'al');
      // Before the debounce elapses, no search yet.
      await tester.pump(const Duration(milliseconds: 100));
      expect(queries, isEmpty);

      // After the debounce window, the search runs once and renders results.
      await tester.pump(const Duration(milliseconds: 300));
      await tester.pumpAndSettle();

      expect(queries, ['al']);
      expect(find.text('Alice Example'), findsOneWidget);
      expect(find.text('alice@example.com'), findsOneWidget);
    });

    testWidgets('selecting a result calls onAdd with the OID and name', (
      tester,
    ) async {
      String? addedOid;
      String? addedName;
      await _pumpSearch(
        tester,
        onSearch: (q) async => [_user('Bob Builder')],
        onAdd: (oid, name) async {
          addedOid = oid;
          addedName = name;
        },
      );

      await tester.enterText(find.byType(TextField), 'bo');
      await tester.pump(const Duration(milliseconds: 300));
      await tester.pumpAndSettle();

      await tester.tap(find.text('Bob Builder'));
      await tester.pumpAndSettle();

      expect(addedOid, '00000000-0000-0000-0000-000000000001');
      expect(addedName, 'Bob Builder');
      // Field resets after a successful add.
      expect(find.text('Bob Builder'), findsNothing);
    });

    testWidgets('shows an error message when the search fails', (tester) async {
      await _pumpSearch(
        tester,
        onSearch: (q) async => throw Exception('boom'),
        onAdd: (_, _) async {},
      );

      await tester.enterText(find.byType(TextField), 'al');
      await tester.pump(const Duration(milliseconds: 300));
      await tester.pumpAndSettle();

      expect(find.text('Directory search unavailable.'), findsOneWidget);
    });

    testWidgets('a stale in-flight search does not overwrite newer results', (
      tester,
    ) async {
      // First query resolves slowly; second resolves fast. The slow one must
      // not clobber the fast one's results.
      await _pumpSearch(
        tester,
        onSearch: (q) async {
          if (q == 'al') {
            await Future<void>.delayed(const Duration(seconds: 2));
            return [_user('Stale Alice')];
          }
          return [_user('Fresh Alex')];
        },
        onAdd: (_, _) async {},
      );

      await tester.enterText(find.byType(TextField), 'al');
      await tester.pump(const Duration(milliseconds: 300));
      // 'al' search is now in flight (2s). Type 'ale' before it resolves.
      await tester.enterText(find.byType(TextField), 'ale');
      await tester.pump(const Duration(milliseconds: 300));
      await tester.pumpAndSettle(const Duration(seconds: 3));

      expect(find.text('Fresh Alex'), findsOneWidget);
      expect(find.text('Stale Alice'), findsNothing);
    });
  });
}
