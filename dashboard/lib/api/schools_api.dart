import 'dart:convert';

import 'api_client.dart';
import 'sessions_api.dart' show ApiException;

/// One lesson period of a school's timetable (#447): when it starts and ends,
/// as school-local `HH:mm` times. One timetable applies to every school day.
class LessonPeriod {
  const LessonPeriod({required this.start, required this.end});

  final String start;
  final String end;

  factory LessonPeriod.fromJson(Map<String, dynamic> json) =>
      LessonPeriod(start: json['start'] as String, end: json['end'] as String);

  Map<String, dynamic> toJson() => {'start': start, 'end': end};
}

/// A school as seen by the admin "Schools" sub-tab (#301): the Entra company
/// [name], whether it is [isActive] (shown to teachers in the Classes school
/// selector), and its [lessonPeriods] (#447), in start order. [name] is the
/// school's identity — toggles and timetables are keyed by it.
class School {
  School({
    required this.name,
    required this.isActive,
    this.lessonPeriods = const <LessonPeriod>[],
  });

  final String name;
  final bool isActive;
  final List<LessonPeriod> lessonPeriods;

  factory School.fromJson(Map<String, dynamic> json) => School(
    name: json['name'] as String,
    isActive: json['isActive'] as bool,
    lessonPeriods: [
      for (final e in json['lessonPeriods'] as List<dynamic>)
        LessonPeriod.fromJson(e as Map<String, dynamic>),
    ],
  );
}

/// Client for the admin schools-management endpoints (#301), all admin-gated on
/// `/admin/schools`: list every school with its active state and lesson
/// periods, toggle activation, and replace the lesson periods (#447). Only
/// active schools reach teachers via `/directory/schools`.
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

  /// Replace the lesson periods of [name] with [periods] (#447): the whole
  /// timetable, one for every school day. The backend refuses a period that
  /// doesn't end after it starts, or periods that overlap. Returns the
  /// resulting [School], its periods in start order.
  Future<School> setLessonPeriods(
    String name,
    List<LessonPeriod> periods,
  ) async {
    final res = await _client.put(
      'admin/schools/lesson-periods',
      headers: {'Content-Type': 'application/json'},
      body: jsonEncode({
        'name': name,
        'lessonPeriods': [for (final p in periods) p.toJson()],
      }),
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
