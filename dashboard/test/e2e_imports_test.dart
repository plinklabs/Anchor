import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

/// `flutter drive` on the web serves the app from integration_test/ and lib/
/// (through `package:`), so an integration test that reaches into test/ by a
/// relative path compiles in a VM run but not in CI's Chrome (#601: three e2e
/// files imported `../test/support/text_contrast.dart`). A helper both sides
/// share lives in integration_test/support/, and test/ imports it from there.
void main() {
  final files =
      Directory('integration_test')
          .listSync(recursive: true)
          .whereType<File>()
          .where((f) => f.path.endsWith('.dart'))
          .toList()
        ..sort((a, b) => a.path.compareTo(b.path));
  final root = Directory('integration_test').absolute.uri;

  test('there are integration test files to check', () {
    expect(files, isNotEmpty);
  });

  for (final file in files) {
    final name = file.path.replaceAll(r'\', '/');
    test('$name imports nothing outside integration_test/', () {
      final directives = RegExp(
        r'''^\s*(?:import|export|part)\s+['"]([^'"]+)['"]''',
        multiLine: true,
      );
      for (final match in directives.allMatches(file.readAsStringSync())) {
        final target = match.group(1)!;
        if (target.contains(':')) continue; // package:, dart:
        final resolved = file.absolute.uri.resolve(target);
        expect(
          resolved.path.startsWith(root.path),
          isTrue,
          reason:
              '$name imports $target, which is outside integration_test/: '
              'the web e2e run cannot load it (#601). Put a shared helper in '
              'integration_test/support/.',
        );
      }
    });
  }
}
