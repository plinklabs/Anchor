import 'package:anchor_dashboard/pages/classes_page.dart';
import 'package:flutter_test/flutter_test.dart';

// The New class form fills in a school year worked out from today's date
// (#152). These pin that rule on fixed dates, so they hold whenever the suite
// runs. The e2e that creates a class asks the same function for today's year
// instead of hard-coding one, which went stale on 1 August 2026 (#375).
void main() {
  test('before August, the school year that started last September runs', () {
    expect(schoolYearFor(DateTime(2026, 1, 1)), '2025-2026');
    expect(schoolYearFor(DateTime(2026, 6, 30)), '2025-2026');
    expect(schoolYearFor(DateTime(2026, 7, 31, 23, 59, 59)), '2025-2026');
  });

  test('from 1 August, the school year that is about to start runs', () {
    expect(schoolYearFor(DateTime(2026, 8, 1)), '2026-2027');
    expect(schoolYearFor(DateTime(2026, 10, 1)), '2026-2027');
    expect(schoolYearFor(DateTime(2026, 12, 31, 23, 59, 59)), '2026-2027');
  });

  test('New Year does not change the school year', () {
    expect(schoolYearFor(DateTime(2026, 12, 31)), '2026-2027');
    expect(schoolYearFor(DateTime(2027, 1, 1)), '2026-2027');
  });
}
