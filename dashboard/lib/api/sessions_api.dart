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
  ClassSummary({
    required this.id,
    required this.name,
    required this.schoolYear,
    this.schoolTag,
    this.classCode,
  });
  final String id;
  final String name;
  final String schoolYear;

  /// Entra `Company` value scoping every Graph query for this class (#96).
  /// Null on legacy classes; set via PATCH /classes/{id}.
  final String? schoolTag;

  /// Entra `Department` value used as the class code (#96). Not unique
  /// across the Arcadia group, so always read in tandem with [schoolTag].
  final String? classCode;

  factory ClassSummary.fromJson(Map<String, dynamic> json) => ClassSummary(
    id: json['id'] as String,
    name: json['name'] as String,
    schoolYear: json['schoolYear'] as String,
    schoolTag: json['schoolTag'] as String?,
    classCode: json['classCode'] as String?,
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
    required this.className,
    required this.joinCode,
    required this.startedAt,
    required this.endedAt,
    required this.summaries,
    required this.recentEvents,
    required this.participants,
    required this.bundles,
    required this.grants,
  });
  final String id;
  final String classId;
  final String className;
  final String joinCode;
  final DateTime startedAt;
  final DateTime? endedAt;
  final List<SessionEventSummary> summaries;
  final List<SessionRecentEvent> recentEvents;
  final List<SessionParticipantInfo> participants;
  final List<SessionBundleInfo> bundles;
  final List<SessionUnblockGrantInfo> grants;

  factory SessionDetail.fromJson(Map<String, dynamic> json) => SessionDetail(
    id: json['id'] as String,
    classId: json['classId'] as String,
    className: json['className'] as String? ?? '',
    joinCode: json['joinCode'] as String? ?? '',
    startedAt: DateTime.parse(json['startedAt'] as String),
    endedAt: json['endedAt'] == null
        ? null
        : DateTime.parse(json['endedAt'] as String),
    summaries: ((json['summaries'] as List<dynamic>?) ?? const <dynamic>[])
        .map((e) => SessionEventSummary.fromJson(e as Map<String, dynamic>))
        .toList(growable: false),
    recentEvents: ((json['recentEvents'] as List<dynamic>?) ?? const <dynamic>[])
        .map((e) => SessionRecentEvent.fromJson(e as Map<String, dynamic>))
        .toList(growable: false),
    participants: ((json['participants'] as List<dynamic>?) ?? const <dynamic>[])
        .map((e) => SessionParticipantInfo.fromJson(e as Map<String, dynamic>))
        .toList(growable: false),
    bundles: ((json['bundles'] as List<dynamic>?) ?? const <dynamic>[])
        .map((e) => SessionBundleInfo.fromJson(e as Map<String, dynamic>))
        .toList(growable: false),
    grants: ((json['grants'] as List<dynamic>?) ?? const <dynamic>[])
        .map((e) => SessionUnblockGrantInfo.fromJson(e as Map<String, dynamic>))
        .toList(growable: false),
  );
}

class SessionRecentEvent {
  SessionRecentEvent({
    required this.id,
    required this.userId,
    required this.kind,
    required this.payloadJson,
    required this.occurredAt,
  });
  final String id;
  final String userId;
  final String kind;
  final String payloadJson;
  final DateTime occurredAt;

  factory SessionRecentEvent.fromJson(Map<String, dynamic> json) =>
      SessionRecentEvent(
        id: json['id'] as String,
        userId: json['userId'] as String,
        kind: (json['kind'] as Object).toString(),
        payloadJson: json['payloadJson'] as String? ?? '',
        occurredAt: DateTime.parse(json['occurredAt'] as String),
      );
}

class SessionParticipantInfo {
  SessionParticipantInfo({
    required this.userId,
    required this.displayName,
    required this.joinedAt,
    required this.declinedAt,
    required this.leftAt,
  });
  final String userId;
  final String displayName;
  final DateTime? joinedAt;
  final DateTime? declinedAt;
  final DateTime? leftAt;

  factory SessionParticipantInfo.fromJson(Map<String, dynamic> json) =>
      SessionParticipantInfo(
        userId: json['userId'] as String,
        displayName: json['displayName'] as String,
        joinedAt: json['joinedAt'] == null
            ? null
            : DateTime.parse(json['joinedAt'] as String),
        declinedAt: json['declinedAt'] == null
            ? null
            : DateTime.parse(json['declinedAt'] as String),
        leftAt: json['leftAt'] == null
            ? null
            : DateTime.parse(json['leftAt'] as String),
      );
}

class SessionBundleInfo {
  SessionBundleInfo({required this.id, required this.name});
  final String id;
  final String name;

  factory SessionBundleInfo.fromJson(Map<String, dynamic> json) =>
      SessionBundleInfo(
        id: json['id'] as String,
        name: json['name'] as String,
      );
}

class SessionUnblockGrantInfo {
  SessionUnblockGrantInfo({
    required this.userId,
    required this.displayName,
    required this.host,
    required this.grantedAt,
  });
  final String userId;
  final String displayName;
  final String host;
  final DateTime grantedAt;

  factory SessionUnblockGrantInfo.fromJson(Map<String, dynamic> json) =>
      SessionUnblockGrantInfo(
        userId: json['userId'] as String,
        displayName: json['displayName'] as String,
        host: json['host'] as String,
        grantedAt: DateTime.parse(json['grantedAt'] as String),
      );
}

/// Per-(student, kind) aggregate written when the session ends (#77). The
/// raw event log is pruned after 30 days; these counts survive indefinitely
/// so the dashboard can still say "47 foreground changes off-list, 12 blocked
/// URLs" long after the underlying rows are gone.
class SessionEventSummary {
  SessionEventSummary({
    required this.userId,
    required this.kind,
    required this.count,
    required this.firstAt,
    required this.lastAt,
  });
  final String userId;
  final String kind;
  final int count;
  final DateTime firstAt;
  final DateTime lastAt;

  factory SessionEventSummary.fromJson(Map<String, dynamic> json) =>
      SessionEventSummary(
        userId: json['userId'] as String,
        kind: json['kind'] as String,
        count: json['count'] as int,
        firstAt: DateTime.parse(json['firstAt'] as String),
        lastAt: DateTime.parse(json['lastAt'] as String),
      );
}

class SessionHistoryEntry {
  SessionHistoryEntry({
    required this.id,
    required this.classId,
    required this.className,
    required this.startedAt,
    required this.endedAt,
  });
  final String id;
  final String classId;
  final String className;
  final DateTime startedAt;
  final DateTime endedAt;

  factory SessionHistoryEntry.fromJson(Map<String, dynamic> json) =>
      SessionHistoryEntry(
        id: json['id'] as String,
        classId: json['classId'] as String,
        className: json['className'] as String,
        startedAt: DateTime.parse(json['startedAt'] as String),
        endedAt: DateTime.parse(json['endedAt'] as String),
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

  Future<List<SessionHistoryEntry>> history({int limit = 50, int offset = 0}) async {
    final res = await _client.get('sessions/history?limit=$limit&offset=$offset');
    _ensureOk(res);
    final list = jsonDecode(res.body) as List<dynamic>;
    return list
        .map((e) => SessionHistoryEntry.fromJson(e as Map<String, dynamic>))
        .toList(growable: false);
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
