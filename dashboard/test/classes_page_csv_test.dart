import 'package:anchor_dashboard/pages/classes_page.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  group('parseRosterCsv', () {
    test('reads upn rows, tolerating quotes and blank lines', () {
      const csv = '''
upn
alice@school.be
"bob@school.be"

charlie@school.be
''';
      final result = parseRosterCsv(csv);
      expect(result.error, isNull);
      expect(result.rows, hasLength(3));
      expect(result.rows[0].upn, 'alice@school.be');
      expect(result.rows[1].upn, 'bob@school.be');
      expect(result.rows[2].upn, 'charlie@school.be');
    });

    test('ignores extra columns and finds upn regardless of position', () {
      const csv = '''
display_name,upn
Alice,alice@school.be
''';
      final result = parseRosterCsv(csv);
      expect(result.rows, hasLength(1));
      expect(result.rows.first.upn, 'alice@school.be');
    });

    test('skips rows with a blank upn cell', () {
      const csv = '''
display_name,upn
Alice,
Bob,bob@school.be
''';
      final result = parseRosterCsv(csv);
      expect(result.rows, hasLength(1));
      expect(result.rows.first.upn, 'bob@school.be');
    });

    test('rejects header without upn', () {
      const csv = '''
display_name,entra_oid
Alice,00000000-0000-0000-0000-000000000001
''';
      final result = parseRosterCsv(csv);
      expect(result.rows, isEmpty);
      expect(result.error, contains('upn'));
    });

    test('treats empty input as error', () {
      final result = parseRosterCsv('   \n  \n');
      expect(result.rows, isEmpty);
      expect(result.error, isNotNull);
    });
  });
}
