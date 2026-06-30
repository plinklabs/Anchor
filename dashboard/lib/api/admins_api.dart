import 'dart:convert';

import 'api_client.dart';
import 'sessions_api.dart' show ApiException;

/// A user as seen by the "Manage admins" surface (#300) — either a current
/// admin or a promotion candidate. [entraOid] disambiguates two people who
/// share a display name; [role] is `Admin` in the admins list and the
/// underlying Teacher/Student role in candidate results.
class AdminUser {
  AdminUser({
    required this.id,
    required this.displayName,
    required this.entraOid,
    required this.role,
  });

  final String id;
  final String displayName;
  final String entraOid;
  final String role;

  factory AdminUser.fromJson(Map<String, dynamic> json) => AdminUser(
    id: json['id'] as String,
    displayName: json['displayName'] as String,
    entraOid: (json['entraOid'] as Object).toString(),
    role: (json['role'] as Object).toString(),
  );
}

/// Client for the admin-management endpoints (#300). Listing/search are
/// admin-gated reads on `/admin/users`; promotion and demotion reuse the
/// role-changing verbs on `/me` so all role mutations live in one place.
class AdminsApi {
  AdminsApi(this._client);
  final ApiClient _client;

  /// The current admins, name-ordered.
  Future<List<AdminUser>> listAdmins() async {
    final res = await _client.get('admin/users/admins');
    _ensureOk(res);
    final list = jsonDecode(res.body) as List<dynamic>;
    return list
        .map((e) => AdminUser.fromJson(e as Map<String, dynamic>))
        .toList(growable: false);
  }

  /// Non-admin users (who have signed in at least once) matching [query], for
  /// the add-admin picker. An empty result means nobody matches — the person
  /// must sign in to the dashboard before they can be promoted.
  Future<List<AdminUser>> searchCandidates(String query) async {
    final res = await _client.get(
      'admin/users/candidates?query=${Uri.encodeQueryComponent(query)}',
    );
    _ensureOk(res);
    final list = jsonDecode(res.body) as List<dynamic>;
    return list
        .map((e) => AdminUser.fromJson(e as Map<String, dynamic>))
        .toList(growable: false);
  }

  /// Promote a signed-in user (by internal id) to admin.
  Future<void> promote(String userId) async {
    final res = await _client.post(
      'me/promote',
      headers: {'Content-Type': 'application/json'},
      body: jsonEncode({'userId': userId}),
    );
    _ensureOk(res);
  }

  /// Demote an admin (by internal id) back to a non-admin role. The server
  /// returns 409 if this is the last remaining admin.
  Future<void> demote(String userId) async {
    final res = await _client.post(
      'me/demote',
      headers: {'Content-Type': 'application/json'},
      body: jsonEncode({'userId': userId}),
    );
    _ensureOk(res);
  }

  void _ensureOk(dynamic res) {
    if (res.statusCode < 200 || res.statusCode >= 300) {
      throw ApiException(res.statusCode, res.body);
    }
  }
}
