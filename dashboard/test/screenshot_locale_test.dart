import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

/// Issue #377: the dashboard follows the browser language (#321), and the
/// website screenshot generator drives it through the Playwright library,
/// which gives a browser context with no `locale` the host's language. On a
/// Dutch machine it wrote Dutch shots into the English website. The generator
/// now opens every context with `locale: LOCALE`, pinned to US English. Nothing
/// runs the generator in CI, and an English host would never show a missing
/// pin, so this check catches it under `flutter test`.
void main() {
  final source = File(
    'screenshots/generate-screenshots.mjs',
  ).readAsStringSync();

  test('the screenshot generator pins its language to US English', () {
    expect(
      source,
      contains("const LOCALE = 'en-US';"),
      reason:
          'The website that embeds the shots is English, so the generator '
          "must shoot in US English whatever the host's language (#377).",
    );
  });

  test('every browser context the generator opens uses the pin', () {
    final contexts = RegExp(
      r'newContext\(([^)]*)\)',
    ).allMatches(source).toList();
    expect(contexts, isNotEmpty);
    for (final context in contexts) {
      expect(
        context.group(1),
        contains('locale: LOCALE'),
        reason:
            'A context opened without `locale: LOCALE` takes the host '
            "language, so its shots don't match the website (#377): "
            '${context.group(0)}',
      );
    }
  });
}
