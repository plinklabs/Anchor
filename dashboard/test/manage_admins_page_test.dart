import 'package:anchor_dashboard/api/admins_api.dart';
import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/sessions_api.dart' show ApiException;
import 'package:anchor_dashboard/pages/manage_admins_page.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

// Widget tests for the "Manage admins" page (#300): they drive the page against
// a scripted fake AdminsApi to prove the list renders, search surfaces
// candidates, promote/demote call through, and the last-admin 409 is shown as a
// human message. Layout/font/composition is covered by the integration test.

ApiClient _dummyClient() => ApiClient(
  baseUrl: Uri.parse('http://localhost'),
  tokenProvider: () async => null,
);

AdminUser _user(String id, String name, {String role = 'Admin'}) =>
    AdminUser(id: id, displayName: name, entraOid: '$id-oid', role: role);

class _FakeAdmins extends AdminsApi {
  _FakeAdmins({
    required this.admins,
    this.candidates = const [],
    this.demoteThrows,
  }) : super(_dummyClient());

  List<AdminUser> admins;
  List<AdminUser> candidates;
  Object? demoteThrows;

  final List<String> promoted = [];
  final List<String> demoted = [];
  String? lastQuery;

  @override
  Future<List<AdminUser>> listAdmins() async => admins;

  @override
  Future<List<AdminUser>> searchCandidates(String query) async {
    lastQuery = query;
    return candidates;
  }

  @override
  Future<void> promote(String userId) async {
    promoted.add(userId);
    // Mirror the server: the promoted user joins the admin list.
    final match = candidates.where((c) => c.id == userId).toList();
    if (match.isNotEmpty) {
      admins = [...admins, _user(userId, match.first.displayName)];
    }
  }

  @override
  Future<void> demote(String userId) async {
    if (demoteThrows != null) throw demoteThrows!;
    demoted.add(userId);
    admins = admins.where((a) => a.id != userId).toList();
  }
}

Widget _host(AdminsApi admins) =>
    MaterialApp(home: ManageAdminsPage(admins: admins));

void _bigWindow(WidgetTester tester) {
  tester.view.physicalSize = const Size(1400, 1000);
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.resetPhysicalSize);
  addTearDown(tester.view.resetDevicePixelRatio);
}

void main() {
  testWidgets('lists current admins on load', (tester) async {
    _bigWindow(tester);
    final api = _FakeAdmins(
      admins: [_user('a1', 'Alice Admin'), _user('a2', 'Bob Admin')],
    );

    await tester.pumpWidget(_host(api));
    await tester.pumpAndSettle();

    expect(find.text('Alice Admin'), findsOneWidget);
    expect(find.text('Bob Admin'), findsOneWidget);
    expect(find.byKey(const Key('admin-remove-a1')), findsOneWidget);
    expect(find.byKey(const Key('admin-remove-a2')), findsOneWidget);
  });

  testWidgets('typing a query surfaces candidates and promote calls through', (
    tester,
  ) async {
    _bigWindow(tester);
    final api = _FakeAdmins(
      admins: [_user('a1', 'Alice Admin')],
      candidates: [_user('t1', 'Tina Teacher', role: 'Teacher')],
    );

    await tester.pumpWidget(_host(api));
    await tester.pumpAndSettle();

    await tester.enterText(
      find.byKey(const Key('manage-admins-search')),
      'tina',
    );
    await tester.pumpAndSettle();
    expect(api.lastQuery, 'tina');
    expect(find.text('Tina Teacher'), findsOneWidget);

    await tester.tap(find.byKey(const Key('admin-add-t1')));
    await tester.pumpAndSettle();

    expect(api.promoted, ['t1']);
    // Promoted user now appears in the admin list, and the search was cleared
    // so the candidate add-control is gone.
    expect(find.byKey(const Key('admin-remove-t1')), findsOneWidget);
    expect(find.byKey(const Key('admin-add-t1')), findsNothing);
  });

  testWidgets('empty search results explain the sign-in requirement', (
    tester,
  ) async {
    _bigWindow(tester);
    final api = _FakeAdmins(admins: [_user('a1', 'Alice Admin')]);

    await tester.pumpWidget(_host(api));
    await tester.pumpAndSettle();

    await tester.enterText(
      find.byKey(const Key('manage-admins-search')),
      'nobody',
    );
    await tester.pumpAndSettle();

    expect(
      find.byKey(const Key('manage-admins-no-candidates')),
      findsOneWidget,
    );
  });

  testWidgets('removing an admin confirms then demotes', (tester) async {
    _bigWindow(tester);
    final api = _FakeAdmins(
      admins: [_user('a1', 'Alice Admin'), _user('a2', 'Bob Admin')],
    );

    await tester.pumpWidget(_host(api));
    await tester.pumpAndSettle();

    await tester.tap(find.byKey(const Key('admin-remove-a2')));
    await tester.pumpAndSettle();

    // Confirm dialog, then proceed.
    expect(find.text('Remove admin?'), findsOneWidget);
    await tester.tap(find.widgetWithText(FilledButton, 'Remove'));
    await tester.pumpAndSettle();

    expect(api.demoted, ['a2']);
    expect(find.text('Bob Admin'), findsNothing);
    expect(find.text('Alice Admin'), findsOneWidget);
  });

  testWidgets('last-admin 409 surfaces a human message', (tester) async {
    _bigWindow(tester);
    final api = _FakeAdmins(
      admins: [_user('a1', 'Alice Admin')],
      demoteThrows: ApiException(409, 'Cannot remove the last admin.'),
    );

    await tester.pumpWidget(_host(api));
    await tester.pumpAndSettle();

    await tester.tap(find.byKey(const Key('admin-remove-a1')));
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(FilledButton, 'Remove'));
    await tester.pumpAndSettle();

    expect(find.byKey(const Key('manage-admins-error')), findsOneWidget);
    expect(find.textContaining('last admin'), findsOneWidget);
    // The admin stays in the list — nothing was removed.
    expect(find.text('Alice Admin'), findsOneWidget);
  });
}
