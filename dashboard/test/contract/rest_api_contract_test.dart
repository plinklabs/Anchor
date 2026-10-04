import 'dart:collection';
import 'dart:convert';
import 'dart:io';

import 'package:anchor_dashboard/api/admins_api.dart';
import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/bundles_api.dart';
import 'package:anchor_dashboard/api/classes_api.dart';
import 'package:anchor_dashboard/api/schools_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

import '../support/rest_contract.dart';

// #393: the dashboard's side of contracts/rest.json. Dashboard E2E runs against
// fake APIs, so a backend change to a route or a field the dashboard parses
// passed every check and broke the dashboard in production. With the contract,
// that change fails Backend CI until the file says so, and the file's change
// runs these tests: each lib/api method against a fake backend that serves a
// response built from the file. The method has to send what the file gives
// (method, path, query, body), parse every response the file allows, and read
// exactly the response fields the file lists, nothing the backend isn't held
// to.

/// The lib/api clients, on one [ApiClient] whose requests go to a fake
/// backend.
class _Apis {
  _Apis(ApiClient client)
    : sessions = SessionsApi(client),
      classes = ClassesApi(client),
      bundles = BundlesApi(client),
      admins = AdminsApi(client),
      schools = SchoolsApi(client);

  final SessionsApi sessions;
  final ClassesApi classes;
  final BundlesApi bundles;
  final AdminsApi admins;
  final SchoolsApi schools;
}

/// A backend that records each request and answers it with [body].
class _FakeBackend {
  _FakeBackend(this.body) {
    // ApiClient makes its own http.Client; inside runWithClient that is this
    // one.
    apis = http.runWithClient(
      () => _Apis(
        ApiClient(
          baseUrl: Uri.parse('https://anchor.test/'),
          tokenProvider: () async => null,
        ),
      ),
      () => MockClient((request) async {
        requests.add(request);
        return http.Response(
          body,
          200,
          headers: {'content-type': 'application/json; charset=utf-8'},
        );
      }),
    );
  }

  final String body;
  final requests = <http.Request>[];
  late final _Apis apis;
}

/// One call the dashboard makes: the contract route it goes to, the lib/api
/// method, how the tests call it (path parameters as `p1`, `p2`, … in order),
/// and how that method parses the response body, for the reads check.
class _Call {
  const _Call(this.route, this.method, this.invoke, [this.parse, this.how]);

  final String route;
  final String method;
  final Future<Object?> Function(_Apis api) invoke;
  final Object? Function(Object? json)? parse;

  /// Which of the method's calls this is, when the tests make more than one.
  final String? how;

  String get name => '$route (${how == null ? method : '$method, $how'})';
}

List<T> _list<T>(Object? json, T Function(Map<String, dynamic> e) parse) => [
  for (final e in json as List) parse(e as Map<String, dynamic>),
];

Map<String, dynamic> _map(Object? json) => json as Map<String, dynamic>;

final _entry = BundleEntry(
  kind: BundleEntryKind.domain,
  value: 'example.com',
  matchType: BundleEntryMatchType.suffix,
);

final _calls = <_Call>[
  _Call(
    'GET me',
    'SessionsApi.me',
    (a) => a.sessions.me(),
    (j) => MeResponse.fromJson(_map(j)),
  ),
  _Call(
    'GET sessions/history',
    'SessionsApi.history',
    (a) => a.sessions.history(),
    (j) => _list(j, SessionHistoryEntry.fromJson),
  ),
  _Call(
    'GET sessions/active',
    'SessionsApi.activeSessions',
    (a) => a.sessions.activeSessions(),
    (j) => _list(j, ActiveSession.fromJson),
  ),
  _Call(
    'GET sessions/{id}',
    'SessionsApi.getSession',
    (a) => a.sessions.getSession('p1'),
    (j) => SessionDetail.fromJson(_map(j)),
  ),
  _Call(
    'GET classes',
    'SessionsApi.classes',
    (a) => a.sessions.classes(),
    (j) => _list(j, ClassSummary.fromJson),
  ),
  _Call(
    'POST sessions',
    'SessionsApi.startSession',
    (a) => a.sessions.startSession('c1', bundleIds: ['b1']),
    (j) => StartSessionResponse.fromJson(_map(j)),
  ),
  _Call(
    'POST sessions/{id}/end',
    'SessionsApi.endSession',
    (a) => a.sessions.endSession('p1'),
  ),
  _Call(
    'PUT sessions/{id}/bundles',
    'SessionsApi.updateBundles',
    (a) => a.sessions.updateBundles('p1', ['b1']),
    (j) => _list(_map(j)['bundles'], SessionBundleInfo.fromJson),
  ),
  _Call(
    'GET sessions/{id}/unblock-requests',
    'SessionsApi.unblockRequests',
    (a) => a.sessions.unblockRequests('p1'),
    (j) => _list(j, UnblockRequestSummary.fromJson),
  ),
  _Call(
    'POST sessions/{id}/unblock',
    'SessionsApi.approveUnblock',
    (a) => a.sessions.approveUnblock('p1', 'u1', 'example.com'),
  ),
  _Call(
    'POST sessions/{id}/unblock',
    'SessionsApi.approveUnblockForClass',
    (a) => a.sessions.approveUnblockForClass('p1', 'example.com'),
  ),
  _Call(
    'POST classes',
    'ClassesApi.createClass',
    (a) => a.classes.createClass(
      name: '3A',
      schoolYear: '2025-2026',
      schoolTag: 'School',
      classCode: '3A',
    ),
    (j) => ClassSummary.fromJson(_map(j)),
  ),
  _Call(
    'GET classes',
    'ClassesApi.listIncludingArchived',
    (a) => a.classes.listIncludingArchived(),
    (j) => _list(j, ClassSummary.fromJson),
  ),
  _Call(
    'POST classes/{id}/archive',
    'ClassesApi.archiveClass',
    (a) => a.classes.archiveClass('p1'),
    (j) => ClassSummary.fromJson(_map(j)),
  ),
  _Call(
    'POST classes/{id}/unarchive',
    'ClassesApi.unarchiveClass',
    (a) => a.classes.unarchiveClass('p1'),
    (j) => ClassSummary.fromJson(_map(j)),
  ),
  _Call(
    'DELETE classes/{id}',
    'ClassesApi.deleteClass',
    (a) => a.classes.deleteClass('p1'),
  ),
  _Call(
    'DELETE classes/{id}',
    'ClassesApi.deleteClass',
    (a) => a.classes.deleteClass('p1', includeSessions: true),
    null,
    'sessions included',
  ),
  _Call(
    'GET classes/{id}/members',
    'ClassesApi.members',
    (a) => a.classes.members('p1'),
    (j) => ClassMembersResponse.fromJson(_map(j)),
  ),
  _Call(
    'GET directory/schools',
    'ClassesApi.schools',
    (a) => a.classes.schools(),
    (j) => [for (final e in j as List) e as String],
  ),
  _Call(
    'PATCH classes/{id}',
    'ClassesApi.updateCodes',
    (a) => a.classes.updateCodes('p1', schoolTag: 'School', classCode: null),
    (j) => ClassSummary.fromJson(_map(j)),
  ),
  _Call(
    'GET users/search',
    'ClassesApi.searchUsers',
    (a) => a.classes.searchUsers('al', company: 'School'),
    (j) => _list(j, DirectoryUser.fromJson),
  ),
  _Call(
    'POST classes/{id}/members',
    'ClassesApi.addMember',
    (a) => a.classes.addMember('p1', entraOid: 'o1', displayName: 'Al'),
    (j) => ClassMembershipImportResult.fromJson(_map(j)),
  ),
  _Call(
    'DELETE classes/{id}/members/{userId}',
    'ClassesApi.removeMember',
    (a) => a.classes.removeMember('p1', 'p2'),
  ),
  _Call(
    'POST classes/{id}/members/import',
    'ClassesApi.importMembers',
    (a) => a.classes.importMembers('p1', [ImportRow(upn: 'al@school.be')]),
    (j) => _list(_map(j)['results'], ClassMembershipImportResult.fromJson),
  ),
  _Call(
    'POST classes/{id}/members/bulk-import',
    'ClassesApi.bulkImportFromDirectory',
    (a) => a.classes.bulkImportFromDirectory('p1'),
    (j) => _list(_map(j)['results'], ClassMembershipImportResult.fromJson),
  ),
  _Call(
    'GET bundles',
    'BundlesApi.list',
    (a) => a.bundles.list(),
    (j) => _list(j, BundleSummary.fromJson),
  ),
  _Call(
    'GET bundles',
    'BundlesApi.list',
    (a) => a.bundles.list(includeArchived: true),
    (j) => _list(j, BundleSummary.fromJson),
    'archived too',
  ),
  _Call(
    'GET bundles/{id}',
    'BundlesApi.get',
    (a) => a.bundles.get('p1'),
    (j) => BundleDetail.fromJson(_map(j)),
  ),
  _Call(
    'POST bundles',
    'BundlesApi.create',
    (a) => a.bundles.create('Maths', [_entry]),
    (j) => BundleDetail.fromJson(_map(j)),
  ),
  _Call(
    'PUT bundles/{id}',
    'BundlesApi.update',
    (a) => a.bundles.update('p1', 'Maths', [_entry]),
    (j) => BundleDetail.fromJson(_map(j)),
  ),
  _Call(
    'DELETE bundles/{id}',
    'BundlesApi.archive',
    (a) => a.bundles.archive('p1'),
  ),
  _Call(
    'DELETE bundles/{id}',
    'BundlesApi.hardDelete',
    (a) => a.bundles.hardDelete('p1'),
  ),
  _Call(
    'GET admin/users/admins',
    'AdminsApi.listAdmins',
    (a) => a.admins.listAdmins(),
    (j) => _list(j, AdminUser.fromJson),
  ),
  _Call(
    'GET admin/users/candidates',
    'AdminsApi.searchCandidates',
    (a) => a.admins.searchCandidates('al'),
    (j) => _list(j, AdminUser.fromJson),
  ),
  _Call('POST me/promote', 'AdminsApi.promote', (a) => a.admins.promote('u1')),
  _Call('POST me/demote', 'AdminsApi.demote', (a) => a.admins.demote('u1')),
  _Call(
    'GET admin/schools',
    'SchoolsApi.listSchools',
    (a) => a.schools.listSchools(),
    (j) => _list(j, School.fromJson),
  ),
  _Call(
    'POST admin/schools/activation',
    'SchoolsApi.setActive',
    (a) => a.schools.setActive('School', true),
    (j) => School.fromJson(_map(j)),
  ),
];

/// A decoded JSON object that records every field looked up in it, as a
/// path (`bundles[].id`), and hands out what it holds recorded the same way.
class _Recorded extends MapBase<String, dynamic> {
  _Recorded(this._fields, this._path, this._reads);

  final Map<String, dynamic> _fields;
  final String _path;
  final Set<String> _reads;

  Object? _lookUp(Object? key) {
    final path = _path.isEmpty ? '$key' : '$_path.$key';
    _reads.add(path);
    return _record(_fields[key], path, _reads);
  }

  @override
  dynamic operator [](Object? key) => _lookUp(key);

  @override
  bool containsKey(Object? key) {
    _lookUp(key);
    return _fields.containsKey(key);
  }

  @override
  void operator []=(String key, dynamic value) => _fields[key] = value;

  @override
  Iterable<String> get keys => _fields.keys;

  @override
  dynamic remove(Object? key) => _fields.remove(key);

  @override
  void clear() => _fields.clear();
}

Object? _record(Object? json, String path, Set<String> reads) {
  if (json is Map<String, dynamic>) return _Recorded(json, path, reads);
  if (json is List) return [for (final e in json) _record(e, '$path[]', reads)];
  return json;
}

/// The model a parse gives, to tell that [_Call.parse] is the one the lib/api
/// method uses.
String _modelOf(Object? value) => value is List
    ? 'List<${value.isEmpty ? '?' : value.first.runtimeType}>'
    : '${value.runtimeType}';

void main() {
  final contract = RestContract.load();

  test('every route in the contract is called by the dashboard, and every '
      'call goes to a route in the contract', () {
    expect(
      _calls.map((c) => c.route).toSet(),
      unorderedEquals(contract.routes.keys),
    );
  });

  test('every call lib/api makes is checked here', () {
    // A new lib/api method has to be added to _calls, under the route the
    // contract gives it (or the route added to contracts/rest.json).
    final calls = RegExp(r'_client\s*\.\s*(get|post|put|patch|delete)\s*\(');
    final made = Directory('lib/api')
        .listSync()
        .whereType<File>()
        .where((f) => f.path.endsWith('.dart'))
        .map((f) => calls.allMatches(f.readAsStringSync()).length)
        .fold(0, (a, b) => a + b);
    expect(
      _calls.map((c) => c.method).toSet(),
      hasLength(made),
      reason: 'lib/api makes $made calls to the backend.',
    );
  });

  for (final call in _calls) {
    final route = contract.routes[call.route];
    final name = call.name;
    if (route == null) {
      test('$name is in contracts/rest.json', () {
        fail('${call.method} calls ${call.route}, which the contract lacks.');
      });
      continue;
    }

    final sends = '$name sends the method, path, query and body it gives';
    test(sends, () async {
      final served = route.responseBody(route.responseVariants.first);
      final backend = _FakeBackend(served == null ? '' : jsonEncode(served));
      await call.invoke(backend.apis);

      final request = backend.requests.single;
      expect(request.method, route.method);
      expect(request.url.path, '/${route.pathFilledIn}');
      expect(
        request.url.queryParameters.keys,
        everyElement(isIn(route.query)),
        reason: 'The backend binds ${route.query} of the query.',
      );
      if (route.request == null) {
        expect(request.body, isEmpty, reason: 'The backend binds no body.');
        return;
      }
      final type = request.headers['content-type'];
      expect(type, startsWith('application/json'));
      expect(route.requestProblems(jsonDecode(request.body)), isEmpty);
    });

    if (route.response == null) {
      test('$name ignores the response body, as the contract says', () async {
        expect(call.parse, isNull, reason: 'The contract has no response.');
        await call.invoke(_FakeBackend('').apis);
      });
      continue;
    }

    for (final variant in route.responseVariants) {
      test('$name parses the response ${variant.description}', () async {
        final backend = _FakeBackend(jsonEncode(route.responseBody(variant)));
        final parsed = await call.invoke(backend.apis);
        if (parsed is List) {
          expect(parsed, isNotEmpty, reason: 'The response has one element.');
        }
      });
    }

    final reads = '$name reads exactly the response fields it lists';
    test(reads, () async {
      final body = route.responseBody(route.responseVariants.first);
      final lookedUp = <String>{};
      final parse = call.parse;
      expect(parse, isNotNull, reason: 'The contract has a response.');
      final parsed = parse!(_record(body, '', lookedUp));

      expect(
        lookedUp.difference(route.responseFields),
        isEmpty,
        reason: 'The dashboard reads these, which the contract lacks.',
      );
      expect(
        route.responseFields.difference(lookedUp),
        isEmpty,
        reason: "The contract lists these, which the dashboard doesn't read.",
      );
      final fromApi = await call.invoke(_FakeBackend(jsonEncode(body)).apis);
      expect(_modelOf(parsed), _modelOf(fromApi));
    });
  }
}
