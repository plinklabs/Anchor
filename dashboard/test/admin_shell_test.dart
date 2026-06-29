import 'package:anchor_dashboard/widgets/admin_shell.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

// #299: the admin-area chrome — a left vertical sub-nav beside the routed
// sub-page. Guards the presentational shell in isolation (no router/network):
// the rail renders its sub-tabs, marks the active one, and fires onNavigate.

Widget _host({
  required AdminSection section,
  void Function(String location)? onNavigate,
  Widget? child,
}) {
  return MaterialApp(
    home: Scaffold(
      body: AdminShell(
        section: section,
        onNavigate: onNavigate ?? (_) {},
        child: child ?? const SizedBox(key: Key('admin-page-body')),
      ),
    ),
  );
}

void main() {
  testWidgets('renders the vertical sub-nav and the page body', (tester) async {
    tester.view.physicalSize = const Size(1400, 900);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);

    await tester.pumpWidget(_host(section: AdminSection.bundles));
    await tester.pumpAndSettle();

    expect(find.byKey(const Key('admin-nav-bundles')), findsOneWidget);
    expect(find.text('BUNDLES'), findsOneWidget);
    expect(find.byKey(const Key('admin-page-body')), findsOneWidget);
  });

  testWidgets('sub-nav taps fire onNavigate with the sub-route', (tester) async {
    tester.view.physicalSize = const Size(1400, 900);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);

    String? navigatedTo;
    await tester.pumpWidget(_host(
      section: AdminSection.bundles,
      onNavigate: (location) => navigatedTo = location,
    ));
    await tester.pumpAndSettle();

    await tester.tap(find.byKey(const Key('admin-nav-bundles')));
    expect(navigatedTo, '/admin/bundles');
  });
}
