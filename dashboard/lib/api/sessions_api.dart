import 'dart:convert';

import 'package:http/http.dart' as http;

import 'api_client.dart';

class ApiException implements Exception {
  ApiException(this.statusCode, this.message);
  final int statusCode;
  final String message;
  @override
  String toString() => 'ApiException($statusCode): $message';
}

class ClassSummary {
  ClassSummary({required this.id, required this.name, required this.schoolYear});
  final String id;
  final String name;
  final String schoolYear;

  factory ClassSummary.fromJson(Map<String, dynamic> json) => ClassSummary(
    id: json['id'] as String,
    name: json['name'] as String,
    schoolYear: json['schoolYear'] as String,
  );
}

class StartSessionResponse {
  StartSessionResponse({
    required this.id,
    required this.classId,
    required this.joinCode,
    required this.startedAt,
  });
  final String id;
  final String classId;
  final String joinCode;
  final DateTime startedAt;

  factory StartSessionResponse.fromJson(Map<String, dynamic> json) =>
      StartSessionResponse(
        id: json['id'] as String,
        classId: json['classId'] as String,
        joinCode: json['joinCode'] as String,
        startedAt: DateTime.parse(json['startedAt'] as String),
      );
}

class MeResponse {
  MeResponse({
    required this.id,
    required this.displayName,
    required this.role,
  });
  final String id;
  final String displayName;
  final String role;

  factory MeResponse.fromJson(Map<String, dynamic> json) => MeResponse(
    id: json['id'] as String,
    displayName: json['displayName'] as String,
    role: (json['role'] as Object).toString(),
  );
}

class SessionsApi {
  SessionsApi(this._client);
  final ApiClient _client;

  Future<MeResponse> me() async {
    final res = await _client.get('me');
    _ensureOk(res);
    return MeResponse.fromJson(jsonDecode(res.body) as Map<String, dynamic>);
  }

  Future<List<ClassSummary>> classes() async {
    final res = await _client.get('classes');
    _ensureOk(res);
    final list = jsonDecode(res.body) as List<dynamic>;
    return list
        .map((e) => ClassSummary.fromJson(e as Map<String, dynamic>))
        .toList(growable: false);
  }

  Future<StartSessionResponse> startSession(String classId) async {
    final res = await _client.post(
      'sessions',
      headers: {'Content-Type': 'application/json'},
      body: jsonEncode({
        'classId': classId,
        'mode': 'Strict',
        'bundleIds': <String>[],
      }),
    );
    _ensureOk(res);
    return StartSessionResponse.fromJson(
      jsonDecode(res.body) as Map<String, dynamic>,
    );
  }

  Future<void> endSession(String sessionId) async {
    final res = await _client.post('sessions/$sessionId/end');
    _ensureOk(res);
  }

  void _ensureOk(http.Response res) {
    if (res.statusCode < 200 || res.statusCode >= 300) {
      throw ApiException(res.statusCode, res.body);
    }
  }
}
