import 'package:anchor_dashboard/l10n/app_localizations.dart';
import 'package:anchor_dashboard/widgets/anchor_mark.dart';
import 'package:anchor_dashboard/widgets/app_shell.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:plink_design_system/plink_design_system.dart';

// AD1 (#166): the shared dashboard chrome — app scaffold, nav, app-bar. These
// guard the presentational shell in isolation (no router/network): the lockup,
// the nav destinations (with the admin-gated Admin slot), the per-section
// mono eyebrow, and the navigate/sign-out wiring.

Widget _host({
  required AppSection section,
  bool isAdmin = false,
  String? accountName,
  void Function(String location)? onNavigate,
  VoidCallback? onSignOut,
  Widget? child,
}) {
  return MaterialApp(
    localizationsDelegates: AppLocalizations.localizationsDelegates,
    supportedLocales: AppLocalizations.supportedLocales,
    theme: PlinkTheme.paper.copyWith(
      extensions: const <ThemeExtension<dynamic>>[
        PlinkProductAccent(Color(0xFF34357A)),
      ],
    ),
    home: AppShell(
      section: section,
      isAdmin: isAdmin,
      accountName: accountName,
      onSignOut: onSignOut,
      onNavigate: onNavigate ?? (_) {},
      child: child ?? const SizedBox(key: Key('page-body')),
    ),
  );
}

void main() {
  testWidgets('renders the lockup, standing nav, eyebrow and page body', (
    tester,
  ) async {
    tester.view.physicalSize = const Size(1400, 900);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);

    await tester.pumpWidget(_host(section: AppSection.home));
    await tester.pumpAndSettle();

    expect(find.byType(AnchorLockup), findsOneWidget);
    expect(find.byKey(const Key('nav-home')), findsOneWidget);
    expect(find.byKey(const Key('nav-classes')), findsOneWidget);
    expect(find.byKey(const Key('nav-history')), findsOneWidget);
    // Section eyebrow is mono + uppercased by the DS Eyebrow widget.
    expect(find.text('01 · HOME'), findsOneWidget);
    expect(find.byKey(const Key('page-body')), findsOneWidget);
  });

  testWidgets('hides the Admin slot unless the teacher is an admin', (
    tester,
  ) async {
    tester.view.physicalSize = const Size(1400, 900);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);

    await tester.pumpWidget(_host(section: AppSection.home));
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('nav-admin')), findsNothing);

    await tester.pumpWidget(_host(section: AppSection.home, isAdmin: true));
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('nav-admin')), findsOneWidget);
  });

  testWidgets('eyebrow tracks the active section', (tester) async {
    tester.view.physicalSize = const Size(1400, 900);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);

    await tester.pumpWidget(_host(section: AppSection.classes));
    await tester.pumpAndSettle();
    expect(find.text('02 · CLASSES'), findsOneWidget);

    // Detail pages have no section number — just the label.
    await tester.pumpWidget(_host(section: AppSection.session));
    await tester.pumpAndSettle();
    expect(find.text('LIVE SESSION'), findsOneWidget);
  });

  testWidgets('nav taps and sign-out fire their callbacks', (tester) async {
    tester.view.physicalSize = const Size(1400, 900);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);

    String? navigatedTo;
    var signedOut = false;

    await tester.pumpWidget(
      _host(
        section: AppSection.home,
        isAdmin: true,
        accountName: 'Ms Teacher',
        onNavigate: (location) => navigatedTo = location,
        onSignOut: () => signedOut = true,
      ),
    );
    await tester.pumpAndSettle();

    expect(find.text('Ms Teacher'), findsOneWidget);

    await tester.tap(find.byKey(const Key('nav-classes')));
    expect(navigatedTo, '/classes');

    await tester.tap(find.byKey(const Key('nav-admin')));
    expect(navigatedTo, '/admin');

    // The lockup doubles as the home affordance.
    await tester.tap(find.byKey(const Key('lockup-home')));
    expect(navigatedTo, '/');

    await tester.tap(find.byKey(const Key('sign-out')));
    expect(signedOut, isTrue);
  });
}
