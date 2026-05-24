import 'dart:convert';

import 'api_client.dart';
import 'sessions_api.dart' show ApiException;

class BundleSummary {
  BundleSummary({required this.id, required this.name, required this.version});
  final String id;
  final String name;
  final int version;

  factory BundleSummary.fromJson(Map<String, dynamic> json) => BundleSummary(
    id: json['id'] as String,
    name: json['name'] as String,
    version: (json['version'] as num).toInt(),
  );
}

class BundlesApi {
  BundlesApi(this._client);
  final ApiClient _client;

  Future<List<BundleSummary>> list() async {
    final res = await _client.get('bundles');
    if (res.statusCode < 200 || res.statusCode >= 300) {
      throw ApiException(res.statusCode, res.body);
    }
    final list = jsonDecode(res.body) as List<dynamic>;
    return list
        .map((e) => BundleSummary.fromJson(e as Map<String, dynamic>))
        .toList(growable: false);
  }
}
