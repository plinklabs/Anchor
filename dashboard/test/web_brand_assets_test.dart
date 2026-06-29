import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

/// Issue #163 (AF2): the dashboard ships the Anchor brand on its web shell —
/// the paper/light favicon + PWA icons (the teacher surface stays paper) and a
/// manifest carrying the Anchor name and paper colours. These guard the web/
/// metadata and icon set so a `flutter create` regeneration or a dropped asset
/// can't quietly ship the generic Flutter placeholder again. Icons are produced
/// from the mark by design/icons/generate.mjs.
void main() {
  final web = Directory('web');

  /// Width/height from a PNG's IHDR chunk (bytes 16-23, big-endian).
  ({int width, int height}) pngSize(File f) {
    final b = f.readAsBytesSync();
    int be32(int o) =>
        (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
    return (width: be32(16), height: be32(20));
  }

  test('manifest carries the Anchor identity and paper colours', () {
    final manifest =
        jsonDecode(File('${web.path}/manifest.json').readAsStringSync())
            as Map<String, dynamic>;

    expect(manifest['name'], 'Anchor');
    expect(manifest['short_name'], 'Anchor');
    // Teacher surface is paper/light; both colours are the paper background.
    expect(manifest['theme_color'], '#FAF7F2');
    expect(manifest['background_color'], '#FAF7F2');
    // No leftover Flutter placeholder copy.
    expect(manifest['description'], isNot(contains('Flutter')));
  });

  test('index.html title/description no longer carry the placeholder', () {
    final html = File('${web.path}/index.html').readAsStringSync();
    expect(html, contains('<title>Anchor</title>'));
    expect(html, isNot(contains('A new Flutter project')));
    expect(html, isNot(contains('anchor_dashboard')));
  });

  for (final (name, expected) in const [
    ('favicon.png', 32),
    ('icons/Icon-192.png', 192),
    ('icons/Icon-512.png', 512),
    ('icons/Icon-maskable-192.png', 192),
    ('icons/Icon-maskable-512.png', 512),
  ]) {
    test('$name exists and is ${expected}x$expected', () {
      final f = File('${web.path}/$name');
      expect(f.existsSync(), isTrue, reason: 'Missing brand asset: ${f.path}');
      expect(pngSize(f), (width: expected, height: expected));
    });
  }
}
