import 'dart:convert';

import 'api_client.dart';
import 'sessions_api.dart' show ApiException;

enum BundleEntryKind { domain, app }

enum BundleEntryMatchType { exact, wildcard, suffix, signedPublisher }

String bundleEntryKindToJson(BundleEntryKind kind) => switch (kind) {
  BundleEntryKind.domain => 'Domain',
  BundleEntryKind.app => 'App',
};

BundleEntryKind bundleEntryKindFromJson(Object raw) {
  switch (raw.toString().toLowerCase()) {
    case 'domain':
      return BundleEntryKind.domain;
    case 'app':
      return BundleEntryKind.app;
    default:
      throw ArgumentError('Unknown BundleEntryKind: $raw');
  }
}

String bundleEntryMatchTypeToJson(BundleEntryMatchType type) => switch (type) {
  BundleEntryMatchType.exact => 'Exact',
  BundleEntryMatchType.wildcard => 'Wildcard',
  BundleEntryMatchType.suffix => 'Suffix',
  BundleEntryMatchType.signedPublisher => 'SignedPublisher',
};

BundleEntryMatchType bundleEntryMatchTypeFromJson(Object raw) {
  switch (raw.toString().toLowerCase()) {
    case 'exact':
      return BundleEntryMatchType.exact;
    case 'wildcard':
      return BundleEntryMatchType.wildcard;
    case 'suffix':
      return BundleEntryMatchType.suffix;
    case 'signedpublisher':
      return BundleEntryMatchType.signedPublisher;
    default:
      throw ArgumentError('Unknown BundleEntryMatchType: $raw');
  }
}

class BundleSummary {
  BundleSummary({
    required this.id,
    required this.name,
    required this.version,
    required this.isArchived,
    required this.hasBeenUsed,
  });

  final String id;
  final String name;
  final int version;
  final bool isArchived;

  /// True if any past or current session bound this bundle. Hard delete is
  /// only allowed when this is false (#89).
  final bool hasBeenUsed;

  factory BundleSummary.fromJson(Map<String, dynamic> json) => BundleSummary(
    id: json['id'] as String,
    name: json['name'] as String,
    version: (json['version'] as num).toInt(),
    isArchived: (json['isArchived'] as bool?) ?? false,
    hasBeenUsed: (json['hasBeenUsed'] as bool?) ?? false,
  );
}

class BundleEntry {
  BundleEntry({
    required this.kind,
    required this.value,
    required this.matchType,
  });

  final BundleEntryKind kind;
  final String value;
  final BundleEntryMatchType matchType;

  factory BundleEntry.fromJson(Map<String, dynamic> json) => BundleEntry(
    kind: bundleEntryKindFromJson(json['kind'] as Object),
    value: json['value'] as String,
    matchType: bundleEntryMatchTypeFromJson(json['matchType'] as Object),
  );

  Map<String, dynamic> toJson() => {
    'kind': bundleEntryKindToJson(kind),
    'value': value,
    'matchType': bundleEntryMatchTypeToJson(matchType),
  };
}

class BundleDetail {
  BundleDetail({
    required this.id,
    required this.name,
    required this.version,
    required this.isArchived,
    required this.hasBeenUsed,
    required this.entries,
  });

  final String id;
  final String name;
  final int version;
  final bool isArchived;
  final bool hasBeenUsed;
  final List<BundleEntry> entries;

  factory BundleDetail.fromJson(Map<String, dynamic> json) => BundleDetail(
    id: json['id'] as String,
    name: json['name'] as String,
    version: (json['version'] as num).toInt(),
    isArchived: (json['isArchived'] as bool?) ?? false,
    hasBeenUsed: (json['hasBeenUsed'] as bool?) ?? false,
    entries: (json['entries'] as List<dynamic>)
        .map((e) => BundleEntry.fromJson(e as Map<String, dynamic>))
        .toList(growable: false),
  );
}

class BundlesApi {
  BundlesApi(this._client);
  final ApiClient _client;

  Future<List<BundleSummary>> list({bool includeArchived = false}) async {
    final path = includeArchived
        ? 'bundles?includeArchived=true'
        : 'bundles';
    final res = await _client.get(path);
    if (res.statusCode < 200 || res.statusCode >= 300) {
      throw ApiException(res.statusCode, res.body);
    }
    final list = jsonDecode(res.body) as List<dynamic>;
    return list
        .map((e) => BundleSummary.fromJson(e as Map<String, dynamic>))
        .toList(growable: false);
  }

  Future<BundleDetail> get(String id) async {
    final res = await _client.get('bundles/$id');
    _ensureOk(res);
    return BundleDetail.fromJson(
      jsonDecode(res.body) as Map<String, dynamic>,
    );
  }

  Future<BundleDetail> create(String name, List<BundleEntry> entries) async {
    final res = await _client.post(
      'bundles',
      headers: {'Content-Type': 'application/json'},
      body: jsonEncode({
        'name': name,
        'entries': entries.map((e) => e.toJson()).toList(),
      }),
    );
    _ensureOk(res);
    return BundleDetail.fromJson(
      jsonDecode(res.body) as Map<String, dynamic>,
    );
  }

  Future<BundleDetail> update(
    String id,
    String name,
    List<BundleEntry> entries,
  ) async {
    final res = await _client.put(
      'bundles/$id',
      headers: {'Content-Type': 'application/json'},
      body: jsonEncode({
        'name': name,
        'entries': entries.map((e) => e.toJson()).toList(),
      }),
    );
    _ensureOk(res);
    return BundleDetail.fromJson(
      jsonDecode(res.body) as Map<String, dynamic>,
    );
  }

  Future<void> archive(String id) async {
    final res = await _client.delete('bundles/$id');
    _ensureOk(res);
  }

  /// Permanently remove a bundle. The server rejects this with 409 if the
  /// bundle has ever been bound to a session (#89) — callers should fall back
  /// to [archive] in that case.
  Future<void> hardDelete(String id) async {
    final res = await _client.delete('bundles/$id?hard=true');
    _ensureOk(res);
  }

  void _ensureOk(dynamic res) {
    if (res.statusCode < 200 || res.statusCode >= 300) {
      throw ApiException(res.statusCode, res.body);
    }
  }
}
