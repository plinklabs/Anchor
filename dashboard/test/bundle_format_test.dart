import 'dart:convert';

import 'package:anchor_dashboard/api/bundles_api.dart';
import 'package:anchor_dashboard/bundles/bundle_format.dart';
import 'package:flutter_test/flutter_test.dart';

BundleData _sample() => BundleData(
  name: 'Exam apps',
  entries: [
    BundleEntry(
      kind: BundleEntryKind.domain,
      value: 'example.com',
      matchType: BundleEntryMatchType.exact,
    ),
    BundleEntry(
      kind: BundleEntryKind.domain,
      value: '*.khanacademy.org',
      matchType: BundleEntryMatchType.wildcard,
    ),
    BundleEntry(
      kind: BundleEntryKind.app,
      value: 'msedge',
      matchType: BundleEntryMatchType.exact,
    ),
    BundleEntry(
      kind: BundleEntryKind.app,
      value: 'Microsoft.Word',
      matchType: BundleEntryMatchType.signedPublisher,
    ),
  ],
);

void _expectEntriesEqual(List<BundleEntry> got, List<BundleEntry> want) {
  expect(got.length, want.length);
  for (var i = 0; i < want.length; i++) {
    expect(got[i].kind, want[i].kind, reason: 'entry $i kind');
    expect(got[i].value, want[i].value, reason: 'entry $i value');
    expect(got[i].matchType, want[i].matchType, reason: 'entry $i matchType');
  }
}

void main() {
  group('export', () {
    test('single bundle exports as a bare {name, entries} object', () {
      final json = jsonDecode(exportBundleToJson(_sample()));
      expect(json, isA<Map<String, dynamic>>());
      expect(json['name'], 'Exam apps');
      expect(json.containsKey('schemaVersion'), isFalse);
      expect((json['entries'] as List).first, {
        'kind': 'Domain',
        'value': 'example.com',
        'matchType': 'Exact',
      });
    });

    test('export-all wraps bundles in a versioned envelope', () {
      final json =
          jsonDecode(exportBundlesToJson([_sample()])) as Map<String, dynamic>;
      expect(json['schemaVersion'], bundleExportSchemaVersion);
      expect((json['bundles'] as List).length, 1);
    });

    test('export omits server-managed fields', () {
      final json =
          jsonDecode(exportBundleToJson(_sample())) as Map<String, dynamic>;
      expect(json.keys, unorderedEquals(['name', 'entries']));
    });
  });

  group('round-trip', () {
    test('single-bundle export -> import reproduces an equivalent bundle', () {
      final result = parseBundlesJson(exportBundleToJson(_sample()));
      expect(result.ok, isTrue, reason: result.errors.join('\n'));
      expect(result.bundles.length, 1);
      expect(result.bundles.single.name, 'Exam apps');
      _expectEntriesEqual(result.bundles.single.entries, _sample().entries);
    });

    test('export-all envelope -> import reproduces every bundle', () {
      final json = exportBundlesToJson([
        _sample(),
        const BundleData(name: 'Empty-ish', entries: []),
      ]);
      // The second bundle has no entries, which is invalid — the whole file
      // is rejected, proving export-all + import share one validation pass.
      final result = parseBundlesJson(json);
      expect(result.ok, isFalse);
    });
  });

  group('import shapes', () {
    test('accepts a bare array of bundles', () {
      final result = parseBundlesJson(
        '[{"name":"A","entries":[{"kind":"App","value":"msedge","matchType":"Exact"}]}]',
      );
      expect(result.ok, isTrue, reason: result.errors.join('\n'));
      expect(result.bundles.single.name, 'A');
    });

    test('ignores server-managed fields on a bundle and its entries', () {
      final result = parseBundlesJson(
        jsonEncode({
          'id': 'should-be-ignored',
          'version': 99,
          'isArchived': true,
          'hasBeenUsed': true,
          'name': 'Has extras',
          'entries': [
            {
              'kind': 'Domain',
              'value': 'example.com',
              'matchType': 'Exact',
              'id': 'ignored',
            },
          ],
        }),
      );
      expect(result.ok, isTrue, reason: result.errors.join('\n'));
      expect(result.bundles.single.name, 'Has extras');
    });

    test('matches enum values case-insensitively', () {
      final result = parseBundlesJson(
        '{"name":"A","entries":[{"kind":"domain","value":"example.com","matchType":"exact"}]}',
      );
      expect(result.ok, isTrue, reason: result.errors.join('\n'));
      expect(result.bundles.single.entries.single.kind, BundleEntryKind.domain);
    });

    test('trims whitespace from name and entry values', () {
      final result = parseBundlesJson(
        '{"name":"  A  ","entries":[{"kind":"App","value":"  msedge  ","matchType":"Exact"}]}',
      );
      expect(result.bundles.single.name, 'A');
      expect(result.bundles.single.entries.single.value, 'msedge');
    });
  });

  group('import validation rejects', () {
    test('malformed JSON', () {
      final result = parseBundlesJson('{not json');
      expect(result.ok, isFalse);
      expect(result.errors.single, contains('not valid JSON'));
    });

    test('a non-object, non-array root', () {
      expect(parseBundlesJson('42').ok, isFalse);
    });

    test('a missing name', () {
      final result = parseBundlesJson(
        '{"entries":[{"kind":"App","value":"msedge","matchType":"Exact"}]}',
      );
      expect(result.ok, isFalse);
      expect(result.errors.single, contains('"name" is required'));
    });

    test('an empty entries list', () {
      final result = parseBundlesJson('{"name":"A","entries":[]}');
      expect(result.ok, isFalse);
      expect(result.errors.single, contains('at least one entry'));
    });

    test('an unknown kind', () {
      final result = parseBundlesJson(
        '{"name":"A","entries":[{"kind":"Gadget","value":"x.com","matchType":"Exact"}]}',
      );
      expect(result.ok, isFalse);
      expect(result.errors.single, contains('invalid "kind"'));
    });

    test('an unknown matchType', () {
      final result = parseBundlesJson(
        '{"name":"A","entries":[{"kind":"Domain","value":"x.com","matchType":"Fuzzy"}]}',
      );
      expect(result.ok, isFalse);
      expect(result.errors.single, contains('invalid "matchType"'));
    });

    test('a domain with SignedPublisher', () {
      final result = parseBundlesJson(
        '{"name":"A","entries":[{"kind":"Domain","value":"x.com","matchType":"SignedPublisher"}]}',
      );
      expect(result.ok, isFalse);
      expect(result.errors.single, contains('SignedPublisher is not valid'));
    });

    test('an app with Wildcard', () {
      final result = parseBundlesJson(
        '{"name":"A","entries":[{"kind":"App","value":"msedge","matchType":"Wildcard"}]}',
      );
      expect(result.ok, isFalse);
      expect(result.errors.single, contains('Wildcard is not valid'));
    });

    test('an exact app value with a path or .exe suffix', () {
      expect(
        parseBundlesJson(
          r'{"name":"A","entries":[{"kind":"App","value":"C:\\x\\msedge","matchType":"Exact"}]}',
        ).errors.single,
        contains('must not include a path'),
      );
      expect(
        parseBundlesJson(
          '{"name":"A","entries":[{"kind":"App","value":"msedge.exe","matchType":"Exact"}]}',
        ).errors.single,
        contains('must not include the .exe suffix'),
      );
    });

    test('a malformed domain', () {
      final result = parseBundlesJson(
        '{"name":"A","entries":[{"kind":"Domain","value":"not a domain","matchType":"Exact"}]}',
      );
      expect(result.ok, isFalse);
      expect(result.errors.single, contains('is not a valid domain'));
    });

    test('two bundles sharing a name in one file', () {
      final result = parseBundlesJson(
        jsonEncode([
          {
            'name': 'Dup',
            'entries': [
              {'kind': 'App', 'value': 'msedge', 'matchType': 'Exact'},
            ],
          },
          {
            'name': 'dup',
            'entries': [
              {'kind': 'App', 'value': 'word', 'matchType': 'Exact'},
            ],
          },
        ]),
      );
      expect(result.ok, isFalse);
      expect(result.errors.single, contains('duplicate name'));
    });
  });

  group('bundleFileNameStem', () {
    test('slugifies a name', () {
      expect(bundleFileNameStem('Exam apps!'), 'exam-apps');
    });

    test('falls back to "bundle" for an unslugifiable name', () {
      expect(bundleFileNameStem('***'), 'bundle');
    });
  });
}
