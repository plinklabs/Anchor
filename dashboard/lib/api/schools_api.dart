import 'dart:convert';

import 'api_client.dart';
import 'sessions_api.dart' show ApiException;

/// A school as seen by the admin "Schools" sub-tab (#301): the Entra company
/// [name] and whether it is [isActive] (shown to teachers in the Classes school
/// selector). [name] is the school's identity — toggles are keyed by it.
class School {
  School({required this.name, required this.isActive});

  final String name;
  final bool isActive;

  factory School.fromJson(Map<String, dynamic> json) =>
      School(name: json['name'] as String, isActive: json['isActive'] as bool);
}

/// Client for the admin schools-management endpoints (#301), all admin-gated on
/// `/admin/schools`: list every school with its active state, and toggle
/// activation. Only active schools reach teachers via `/directory/schools`.
class SchoolsApi {
  SchoolsApi(this._client);
  final ApiClient _client;

  /// Every known school (live Entra companies merged with persisted state),
  /// name-ordered. A school the admin hasn't touched is active by default.
  Future<List<School>> listSchools() async {
    final res = await _client.get('admin/schools');
    _ensureOk(res);
    final list = jsonDecode(res.body) as List<dynamic>;
    return list
        .map((e) => School.fromJson(e as Map<String, dynamic>))
        .toList(growable: false);
  }

  /// Activate or deactivate [name]. Persists the state (creating the row on
  /// first toggle) and returns the resulting [School].
  Future<School> setActive(String name, bool isActive) async {
    final res = await _client.post(
      'admin/schools/activation',
      headers: {'Content-Type': 'application/json'},
      body: jsonEncode({'name': name, 'isActive': isActive}),
    );
    _ensureOk(res);
    return School.fromJson(jsonDecode(res.body) as Map<String, dynamic>);
  }

  void _ensureOk(dynamic res) {
    if (res.statusCode < 200 || res.statusCode >= 300) {
      throw ApiException(res.statusCode, res.body);
    }
  }
}
