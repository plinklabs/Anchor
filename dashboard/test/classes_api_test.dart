import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/classes_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

// #395: the requests behind archiving and deleting a class. The contract test
// (test/contract/) checks that every query parameter is one the backend binds;
// these pin which ones each call sends, above all that a plain delete never
// asks for the class's sessions to go with it.

/// A ClassesApi whose requests go to a backend answering [body], recorded in
/// [requests].
ClassesApi _api(List<http.Request> requests, {String body = '{}'}) =>
    http.runWithClient(
      () => ClassesApi(
        ApiClient(
          baseUrl: Uri.parse('https://anchor.test/'),
          tokenProvider: () async => null,
        ),
      ),
      () => MockClient((request) async {
        requests.add(request);
        return http.Response(body, 200);
      }),
    );

void main() {
  test('a plain delete leaves the sessions out', () async {
    final requests = <http.Request>[];
    await _api(requests).deleteClass('c1');

    expect(requests.single.method, 'DELETE');
    expect(requests.single.url.path, '/classes/c1');
    expect(requests.single.url.queryParameters, isEmpty);
  });

  test('a delete with sessions says so in the query', () async {
    final requests = <http.Request>[];
    await _api(requests).deleteClass('c1', includeSessions: true);

    expect(requests.single.url.path, '/classes/c1');
    expect(requests.single.url.queryParameters, {'includeSessions': 'true'});
  });

  test('the archived view asks for archived classes', () async {
    final requests = <http.Request>[];
    await _api(requests, body: '[]').listIncludingArchived();

    expect(requests.single.url.path, '/classes');
    expect(requests.single.url.queryParameters, {'includeArchived': 'true'});
  });

  group('isArchivedClassConflict', () {
    test('is a 409 that says the class is archived', () {
      expect(
        isArchivedClassConflict(
          ApiException(409, '{"error":"exists","archived":true}'),
        ),
        isTrue,
      );
    });

    test('is not a 409 for a class in use, or one without a JSON body', () {
      expect(
        isArchivedClassConflict(
          ApiException(409, '{"error":"exists","archived":false}'),
        ),
        isFalse,
      );
      expect(isArchivedClassConflict(ApiException(409, '')), isFalse);
      expect(isArchivedClassConflict(ApiException(409, 'conflict')), isFalse);
    });

    test('is not another status or error', () {
      expect(
        isArchivedClassConflict(ApiException(400, '{"archived":true}')),
        isFalse,
      );
      expect(isArchivedClassConflict(StateError('x')), isFalse);
    });
  });
}
