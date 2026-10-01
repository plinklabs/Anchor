import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

/// Issue #371: the dashboard follows the browser language (#321), and
/// `flutter drive` gives Chrome the host's display language, so an e2e that
/// asserts English copy only passes on an English host. Every integration test
/// therefore starts through `ensureE2eBinding()`
/// (integration_test/support/e2e_binding.dart), which pins the language the
/// app sees. CI's runner is en-US, so a file that skipped the pin would still
/// pass there and only fail on a Dutch machine; this check catches it in CI.
void main() {
  final files =
      Directory('integration_test')
          .listSync()
          .whereType<File>()
          .where((f) => f.path.endsWith('_test.dart'))
          .toList()
        ..sort((a, b) => a.path.compareTo(b.path));

  test('there are integration tests to check', () {
    expect(files, isNotEmpty);
  });

  for (final file in files) {
    final name = file.uri.pathSegments.last;
    test('$name starts through ensureE2eBinding()', () {
      final source = file.readAsStringSync();
      expect(
        source,
        contains(RegExp(r'void main\(\) \{\s+ensureE2eBinding\(')),
        reason:
            '$name must call ensureE2eBinding() first in main() so its copy '
            "doesn't depend on the host's language (#371).",
      );
      expect(
        source,
        isNot(
          contains('IntegrationTestWidgetsFlutterBinding.ensureInitialized'),
        ),
        reason:
            '$name initializes the binding itself, which skips the locale '
            'pin; call ensureE2eBinding() instead (#371).',
      );
    });
  }
}
