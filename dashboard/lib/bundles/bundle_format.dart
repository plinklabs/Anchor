/// Pure serialization + validation for the bundle import/export format (#304).
///
/// This file is platform-free on purpose: the actual file download / upload
/// lives behind `BundleFileIo` (a web-only seam), so everything here — the JSON
/// shape, the round-trip, and the import validation — is exercised by VM unit
/// tests without a browser.
///
/// The documented format is in `docs/bundle-format.md`. In short:
/// * a single exported bundle is a bare object `{ "name", "entries": [...] }`;
/// * "export all" wraps a list in an envelope `{ "schemaVersion", "bundles" }`;
/// * import accepts the envelope, a bare object, or a bare array — server-managed
///   fields (`id`, `version`, `isArchived`, `hasBeenUsed`) are ignored.
library;

import 'dart:convert';

import '../api/bundles_api.dart';

/// Bumped only on a breaking change to the export envelope. Import is lenient
/// and does not require a matching value (older/forgotten envelopes still load).
const int bundleExportSchemaVersion = 1;

// Mirror the backend limits (BundlesController) so a file is rejected with a
// clear message client-side before it ever hits the API.
const int _maxNameLength = 128;
const int _maxValueLength = 512;

final JsonEncoder _encoder = JsonEncoder.withIndent('  ');

/// A bundle reduced to the two fields the format carries: its name and entries.
/// Server-managed fields are deliberately absent.
class BundleData {
  const BundleData({required this.name, required this.entries});

  final String name;
  final List<BundleEntry> entries;
}

/// Outcome of parsing an import file: the bundles that parsed cleanly and a
/// flat list of human-readable errors. A file is only safe to import when
/// [errors] is empty — a single bad entry fails its whole bundle, and any error
/// at all should block the import so nothing is created from a half-valid file.
class BundleImportResult {
  const BundleImportResult({required this.bundles, required this.errors});

  final List<BundleData> bundles;
  final List<String> errors;

  bool get ok => errors.isEmpty;
}

/// Serializes one bundle as the bare object form (matches the worked example in
/// the issue / docs, and round-trips through [parseBundlesJson]).
String exportBundleToJson(BundleData bundle) =>
    _encoder.convert(_bundleToMap(bundle));

/// Serializes many bundles as the envelope form used by "export all".
String exportBundlesToJson(List<BundleData> bundles) => _encoder.convert({
  'schemaVersion': bundleExportSchemaVersion,
  'bundles': bundles.map(_bundleToMap).toList(),
});

Map<String, dynamic> _bundleToMap(BundleData b) => {
  'name': b.name,
  'entries': b.entries.map((e) => e.toJson()).toList(),
};

/// A filesystem-safe filename stem for a single-bundle export, e.g.
/// "Exam apps" -> "exam-apps".
String bundleFileNameStem(String name) {
  final slug = name
      .toLowerCase()
      .replaceAll(RegExp(r'[^a-z0-9]+'), '-')
      .replaceAll(RegExp(r'^-+|-+$'), '');
  return slug.isEmpty ? 'bundle' : slug;
}

/// Parses an import file into validated [BundleData]. Accepts three shapes:
/// the export envelope (`{schemaVersion, bundles: [...]}`), a bare single
/// bundle (`{name, entries: [...]}`), or a bare array of bundles.
BundleImportResult parseBundlesJson(String raw) {
  dynamic decoded;
  try {
    decoded = jsonDecode(raw);
  } on FormatException {
    return const BundleImportResult(
      bundles: [],
      errors: ['The file is not valid JSON.'],
    );
  }

  final List<dynamic> rawBundles;
  if (decoded is Map<String, dynamic>) {
    if (decoded.containsKey('bundles')) {
      final value = decoded['bundles'];
      if (value is! List) {
        return const BundleImportResult(
          bundles: [],
          errors: ['"bundles" must be a list.'],
        );
      }
      rawBundles = value;
    } else {
      // A bare single bundle.
      rawBundles = [decoded];
    }
  } else if (decoded is List) {
    rawBundles = decoded;
  } else {
    return const BundleImportResult(
      bundles: [],
      errors: ['Expected a bundle object or a list of bundles.'],
    );
  }

  final bundles = <BundleData>[];
  final errors = <String>[];
  final seenNames = <String>{};
  final single = rawBundles.length == 1;

  for (var i = 0; i < rawBundles.length; i++) {
    final label = single ? 'Bundle' : 'Bundle #${i + 1}';
    final item = rawBundles[i];
    if (item is! Map<String, dynamic>) {
      errors.add('$label: expected an object.');
      continue;
    }
    final parsed = _parseOne(item, label, errors);
    if (parsed == null) continue;
    if (!seenNames.add(parsed.name.toLowerCase())) {
      errors.add('$label: duplicate name "${parsed.name}" in the file.');
      continue;
    }
    bundles.add(parsed);
  }

  if (bundles.isEmpty && errors.isEmpty) {
    errors.add('No bundles found in the file.');
  }

  return BundleImportResult(bundles: bundles, errors: errors);
}

BundleData? _parseOne(
  Map<String, dynamic> map,
  String label,
  List<String> errors,
) {
  final rawName = map['name'];
  if (rawName is! String || rawName.trim().isEmpty) {
    errors.add('$label: "name" is required.');
    return null;
  }
  final name = rawName.trim();
  if (name.length > _maxNameLength) {
    errors.add('$label: name must be at most $_maxNameLength characters.');
    return null;
  }

  final rawEntries = map['entries'];
  if (rawEntries is! List || rawEntries.isEmpty) {
    errors.add('$label "$name": at least one entry is required.');
    return null;
  }

  final entries = <BundleEntry>[];
  var hadEntryError = false;
  for (var j = 0; j < rawEntries.length; j++) {
    final ref = 'entry #${j + 1}';
    final e = rawEntries[j];
    if (e is! Map<String, dynamic>) {
      errors.add('$label "$name": $ref must be an object.');
      hadEntryError = true;
      continue;
    }

    final BundleEntryKind kind;
    try {
      kind = bundleEntryKindFromJson(e['kind'] as Object? ?? '');
    } on ArgumentError {
      errors.add(
        '$label "$name": $ref has an invalid "kind" (expected Domain or App).',
      );
      hadEntryError = true;
      continue;
    }

    final BundleEntryMatchType matchType;
    try {
      matchType = bundleEntryMatchTypeFromJson(e['matchType'] as Object? ?? '');
    } on ArgumentError {
      errors.add(
        '$label "$name": $ref has an invalid "matchType" '
        '(expected Exact, Wildcard, Suffix, or SignedPublisher).',
      );
      hadEntryError = true;
      continue;
    }

    final rawValue = e['value'];
    if (rawValue is! String || rawValue.trim().isEmpty) {
      errors.add('$label "$name": $ref is missing a "value".');
      hadEntryError = true;
      continue;
    }
    final value = rawValue.trim();
    if (value.length > _maxValueLength) {
      errors.add(
        '$label "$name": $ref value must be at most $_maxValueLength characters.',
      );
      hadEntryError = true;
      continue;
    }

    final shapeError = validateEntryShape(kind, matchType, value);
    if (shapeError != null) {
      errors.add('$label "$name": $ref — $shapeError');
      hadEntryError = true;
      continue;
    }

    entries.add(BundleEntry(kind: kind, value: value, matchType: matchType));
  }

  // One bad entry fails the bundle: importing a partially-valid bundle would
  // silently drop entries, which is worse than rejecting the file.
  if (hadEntryError) return null;
  return BundleData(name: name, entries: entries);
}

/// Validates a single entry's kind/matchType/value combination, mirroring the
/// backend's `ValidateEntryShape` (BundlesController) so import rejects exactly
/// what the API would. Returns null when valid, else a one-line reason.
String? validateEntryShape(
  BundleEntryKind kind,
  BundleEntryMatchType matchType,
  String value,
) {
  switch (kind) {
    case BundleEntryKind.domain:
      if (matchType == BundleEntryMatchType.signedPublisher) {
        return 'SignedPublisher is not valid for a Domain entry.';
      }
      final ok = RegExp(
        r'^(\*\.)?([a-zA-Z0-9]([a-zA-Z0-9-]*[a-zA-Z0-9])?)(\.[a-zA-Z0-9]([a-zA-Z0-9-]*[a-zA-Z0-9])?)+$',
      ).hasMatch(value);
      if (!ok) return '"$value" is not a valid domain.';
      return null;
    case BundleEntryKind.app:
      if (matchType == BundleEntryMatchType.wildcard ||
          matchType == BundleEntryMatchType.suffix) {
        final label = matchType == BundleEntryMatchType.wildcard
            ? 'Wildcard'
            : 'Suffix';
        return '$label is not valid for an App entry.';
      }
      if (matchType == BundleEntryMatchType.exact) {
        if (value.contains('\\') || value.contains('/')) {
          return 'Process name "$value" must not include a path.';
        }
        if (value.toLowerCase().endsWith('.exe')) {
          return 'Process name "$value" must not include the .exe suffix.';
        }
      }
      return null;
  }
}
