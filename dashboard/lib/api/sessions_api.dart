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

  bool get isAdmin => role.toLowerCase() == 'admin';

  factory MeResponse.fromJson(Map<String, dynamic> json) => MeResponse(
    id: json['id'] as String,
    displayName: json['displayName'] as String,
    role: (json['role'] as Object).toString(),
  );
}

class SessionDetail {
  SessionDetail({
    required this.id,
    required this.classId,
    required this.joinCode,
    required this.startedAt,
    required this.endedAt,
  });
  final String id;
  final String classId;
  final String joinCode;
  final DateTime startedAt;
  final DateTime? endedAt;

  factory SessionDetail.fromJson(Map<String, dynamic> json) => SessionDetail(
    id: json['id'] as String,
    classId: json['classId'] as String,
    joinCode: json['joinCode'] as String? ?? '',
    startedAt: DateTime.parse(json['startedAt'] as String),
    endedAt: json['endedAt'] == null
        ? null
        : DateTime.parse(json['endedAt'] as String),
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

  Future<SessionDetail> getSession(String sessionId) async {
    final res = await _client.get('sessions/$sessionId');
    _ensureOk(res);
    return SessionDetail.fromJson(
      jsonDecode(res.body) as Map<String, dynamic>,
    );
  }

  Future<List<ClassSummary>> classes() async {
    final res = await _client.get('classes');
    _ensureOk(res);
    final list = jsonDecode(res.body) as List<dynamic>;
    return list
        .map((e) => ClassSummary.fromJson(e as Map<String, dynamic>))
        .toList(growable: false);
  }

  Future<StartSessionResponse> startSession(
    String classId, {
    List<String> bundleIds = const <String>[],
  }) async {
    final res = await _client.post(
      'sessions',
      headers: {'Content-Type': 'application/json'},
      body: jsonEncode({
        'classId': classId,
        'mode': 'Strict',
        'bundleIds': bundleIds,
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

  Future<List<UnblockRequestSummary>> unblockRequests(String sessionId) async {
    final res = await _client.get('sessions/$sessionId/unblock-requests');
    _ensureOk(res);
    final list = jsonDecode(res.body) as List<dynamic>;
    return list
        .map((e) => UnblockRequestSummary.fromJson(e as Map<String, dynamic>))
        .toList(growable: false);
  }

  Future<void> approveUnblock(String sessionId, String userId, String host) async {
    final res = await _client.post(
      'sessions/$sessionId/unblock',
      headers: {'Content-Type': 'application/json'},
      body: jsonEncode({'userId': userId, 'host': host}),
    );
    _ensureOk(res);
  }

  void _ensureOk(http.Response res) {
    if (res.statusCode < 200 || res.statusCode >= 300) {
      throw ApiException(res.statusCode, res.body);
    }
  }
}

class UnblockRequestRequester {
  UnblockRequestRequester({
    required this.userId,
    required this.displayName,
    required this.requestedAt,
  });
  final String userId;
  final String displayName;
  final DateTime requestedAt;

  factory UnblockRequestRequester.fromJson(Map<String, dynamic> json) =>
      UnblockRequestRequester(
        userId: json['userId'] as String,
        displayName: json['displayName'] as String,
        requestedAt: DateTime.parse(json['requestedAt'] as String),
      );
}

class UnblockRequestSummary {
  UnblockRequestSummary({
    required this.host,
    required this.count,
    required this.firstRequestedAt,
    required this.latestRequestedAt,
    required this.requesters,
  });
  final String host;
  final int count;
  final DateTime firstRequestedAt;
  final DateTime latestRequestedAt;
  final List<UnblockRequestRequester> requesters;

  factory UnblockRequestSummary.fromJson(Map<String, dynamic> json) =>
      UnblockRequestSummary(
        host: json['host'] as String,
        count: json['count'] as int,
        firstRequestedAt: DateTime.parse(json['firstRequestedAt'] as String),
        latestRequestedAt: DateTime.parse(json['latestRequestedAt'] as String),
        requesters: (json['requesters'] as List<dynamic>)
            .map((e) => UnblockRequestRequester.fromJson(e as Map<String, dynamic>))
            .toList(growable: false),
      );
}
