import 'dart:convert';

import 'api_client.dart';
import 'sessions_api.dart' show ApiException, ClassSummary;

class ClassMember {
  ClassMember({
    required this.userId,
    required this.entraOid,
    required this.displayName,
    required this.userRole,
    required this.membershipRole,
    required this.joinedAt,
  });

  final String userId;
  final String entraOid;
  final String displayName;
  final String userRole;
  final String membershipRole;
  final DateTime joinedAt;

  factory ClassMember.fromJson(Map<String, dynamic> json) => ClassMember(
    userId: json['userId'] as String,
    entraOid: json['entraOid'] as String,
    displayName: json['displayName'] as String,
    userRole: (json['userRole'] as Object).toString(),
    membershipRole: (json['membershipRole'] as Object).toString(),
    joinedAt: DateTime.parse(json['joinedAt'] as String),
  );
}

class ClassMembersResponse {
  ClassMembersResponse({
    required this.id,
    required this.name,
    required this.schoolYear,
    this.schoolTag,
    this.classCode,
    required this.members,
  });

  final String id;
  final String name;
  final String schoolYear;
  final String? schoolTag;
  final String? classCode;
  final List<ClassMember> members;

  factory ClassMembersResponse.fromJson(Map<String, dynamic> json) =>
      ClassMembersResponse(
        id: json['id'] as String,
        name: json['name'] as String,
        schoolYear: json['schoolYear'] as String,
        schoolTag: json['schoolTag'] as String?,
        classCode: json['classCode'] as String?,
        members: (json['members'] as List<dynamic>)
            .map((e) => ClassMember.fromJson(e as Map<String, dynamic>))
            .toList(growable: false),
      );
}

class DirectoryUser {
  DirectoryUser({
    required this.entraOid,
    required this.displayName,
    required this.upn,
    this.company,
    this.department,
  });

  final String entraOid;
  final String displayName;
  final String? upn;
  final String? company;
  final String? department;

  factory DirectoryUser.fromJson(Map<String, dynamic> json) => DirectoryUser(
    entraOid: json['entraOid'] as String,
    displayName: json['displayName'] as String,
    upn: json['upn'] as String?,
    company: json['company'] as String?,
    department: json['department'] as String?,
  );
}

enum ClassMembershipImportStatus {
  added,
  alreadyMember,
  notFoundInEntra,
  wrongSchool,
}

ClassMembershipImportStatus _parseImportStatus(Object raw) {
  // Backend serializes the enum by name (e.g. "Added"); be lenient on casing.
  final s = raw.toString().toLowerCase();
  switch (s) {
    case 'added':
      return ClassMembershipImportStatus.added;
    case 'alreadymember':
      return ClassMembershipImportStatus.alreadyMember;
    case 'notfoundinentra':
      return ClassMembershipImportStatus.notFoundInEntra;
    case 'wrongschool':
      return ClassMembershipImportStatus.wrongSchool;
    default:
      return ClassMembershipImportStatus.notFoundInEntra;
  }
}

class ClassMembershipImportResult {
  ClassMembershipImportResult({
    required this.entraOid,
    required this.upn,
    required this.userId,
    required this.status,
    required this.detail,
  });

  final String? entraOid;
  final String? upn;
  final String? userId;
  final ClassMembershipImportStatus status;
  final String? detail;

  factory ClassMembershipImportResult.fromJson(Map<String, dynamic> json) =>
      ClassMembershipImportResult(
        entraOid: json['entraOid'] as String?,
        upn: json['upn'] as String?,
        userId: json['userId'] as String?,
        status: _parseImportStatus(json['status'] as Object),
        detail: json['detail'] as String?,
      );
}

class ImportRow {
  ImportRow({required this.upn, this.role = 'Member'});

  final String upn;
  final String role;

  Map<String, dynamic> toJson() => {'upn': upn, 'role': role};
}

class ClassesApi {
  ClassesApi(this._client);
  final ApiClient _client;

  Future<ClassMembersResponse> members(String classId) async {
    final res = await _client.get('classes/$classId/members');
    _ensureOk(res);
    return ClassMembersResponse.fromJson(
      jsonDecode(res.body) as Map<String, dynamic>,
    );
  }

  /// Lists the school tags (Entra `Company` values) the directory knows about.
  /// Used to populate the school selector on the roster screen (#96).
  Future<List<String>> schools() async {
    final res = await _client.get('directory/schools');
    _ensureOk(res);
    final list = jsonDecode(res.body) as List<dynamic>;
    return list.map((e) => e as String).toList(growable: false);
  }

  /// Sets the class's school tag + class code. Both are overwritten; pass
  /// null to clear. Backend returns the resulting [ClassSummary].
  Future<ClassSummary> updateCodes(
    String classId, {
    required String? schoolTag,
    required String? classCode,
  }) async {
    final res = await _client.patch(
      'classes/$classId',
      headers: {'Content-Type': 'application/json'},
      body: jsonEncode({'schoolTag': schoolTag, 'classCode': classCode}),
    );
    _ensureOk(res);
    return ClassSummary.fromJson(jsonDecode(res.body) as Map<String, dynamic>);
  }

  /// Search the directory for users, optionally scoped to a school. The
  /// roster screen always passes the class's [schoolTag] so the dropdown
  /// can't surface students from other schools (#96).
  Future<List<DirectoryUser>> searchUsers(
    String query, {
    int top = 10,
    String? company,
  }) async {
    final params = <String, String>{
      'q': query,
      'top': '$top',
      if (company != null && company.isNotEmpty) 'company': company,
    };
    final qs = params.entries
        .map(
          (e) =>
              '${Uri.encodeQueryComponent(e.key)}=${Uri.encodeQueryComponent(e.value)}',
        )
        .join('&');
    final res = await _client.get('users/search?$qs');
    _ensureOk(res);
    final list = jsonDecode(res.body) as List<dynamic>;
    return list
        .map((e) => DirectoryUser.fromJson(e as Map<String, dynamic>))
        .toList(growable: false);
  }

  Future<ClassMembershipImportResult> addMember(
    String classId, {
    required String entraOid,
    String? displayName,
    String role = 'Member',
  }) async {
    final res = await _client.post(
      'classes/$classId/members',
      headers: {'Content-Type': 'application/json'},
      body: jsonEncode({
        'entraOid': entraOid,
        if (displayName != null && displayName.isNotEmpty)
          'displayName': displayName,
        'role': role,
      }),
    );
    _ensureOk(res);
    return ClassMembershipImportResult.fromJson(
      jsonDecode(res.body) as Map<String, dynamic>,
    );
  }

  Future<void> removeMember(String classId, String userId) async {
    final res = await _client.delete('classes/$classId/members/$userId');
    _ensureOk(res);
  }

  Future<List<ClassMembershipImportResult>> importMembers(
    String classId,
    List<ImportRow> rows,
  ) async {
    final res = await _client.post(
      'classes/$classId/members/import',
      headers: {'Content-Type': 'application/json'},
      body: jsonEncode({'rows': rows.map((r) => r.toJson()).toList()}),
    );
    _ensureOk(res);
    final body = jsonDecode(res.body) as Map<String, dynamic>;
    return (body['results'] as List<dynamic>)
        .map(
          (e) => ClassMembershipImportResult.fromJson(e as Map<String, dynamic>),
        )
        .toList(growable: false);
  }

  /// Pulls every user from Graph whose `companyName` matches the class's
  /// schoolTag AND whose `department` matches the class's classCode, and
  /// adds them to the roster. The class must have both fields set first
  /// (set via [updateCodes]).
  Future<List<ClassMembershipImportResult>> bulkImportFromDirectory(
    String classId,
  ) async {
    final res = await _client.post('classes/$classId/members/bulk-import');
    _ensureOk(res);
    final body = jsonDecode(res.body) as Map<String, dynamic>;
    return (body['results'] as List<dynamic>)
        .map(
          (e) => ClassMembershipImportResult.fromJson(e as Map<String, dynamic>),
        )
        .toList(growable: false);
  }

  void _ensureOk(dynamic res) {
    if (res.statusCode < 200 || res.statusCode >= 300) {
      throw ApiException(res.statusCode, res.body);
    }
  }
}
