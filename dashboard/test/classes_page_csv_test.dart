import 'package:anchor_dashboard/pages/classes_page.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  group('parseRosterCsv', () {
    test('reads header-mapped rows and ignores invalid GUIDs', () {
      const csv = '''
display_name,entra_oid
Alice,00000000-0000-0000-0000-000000000001
"Bob, Jr.",00000000-0000-0000-0000-000000000002
Bad Row,not-a-guid
''';
      final result = parseRosterCsv(csv);
      expect(result.error, isNull);
      expect(result.rows, hasLength(2));
      expect(result.rows[0].displayName, 'Alice');
      expect(result.rows[0].entraOid, '00000000-0000-0000-0000-000000000001');
      expect(result.rows[1].displayName, 'Bob, Jr.');
    });

    test('accepts reversed column order', () {
      const csv = '''
entra_oid,display_name
00000000-0000-0000-0000-000000000001,Alice
''';
      final result = parseRosterCsv(csv);
      expect(result.rows, hasLength(1));
      expect(result.rows.first.displayName, 'Alice');
    });

    test('rejects header without entra_oid', () {
      const csv = '''
display_name,upn
Alice,alice@example.com
''';
      final result = parseRosterCsv(csv);
      expect(result.rows, isEmpty);
      expect(result.error, contains('entra_oid'));
    });

    test('treats empty input as error', () {
      final result = parseRosterCsv('   \n  \n');
      expect(result.rows, isEmpty);
      expect(result.error, isNotNull);
    });
  });
}
