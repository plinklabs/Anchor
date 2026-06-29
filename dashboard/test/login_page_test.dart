import 'dart:async';

import 'package:anchor_dashboard/api/auth_token_store.dart';
import 'package:anchor_dashboard/auth/msal_auth_service.dart';
import 'package:anchor_dashboard/pages/login_page.dart';
import 'package:anchor_dashboard/widgets/anchor_mark.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:plink_design_system/plink_design_system.dart';

// AD2 (#167): the redesigned sign-in page. These guard the page in isolation
// (no router/network): the paper chrome (identity rule + lockup), the flush-
// left editorial header, the single primary (magenta) Microsoft action, its
// busy state, and the error path when sign-in fails.

class _FakeAuth implements MsalAuthService {
  _FakeAuth({this.account, this.fail = false, this.hangAcquire = false});

  final AccountInfo? account;
  final bool fail;

  /// When true, [acquireToken] never completes — simulating the stalled
  /// silent-token path a day-old cached session can trigger (#303).
  final bool hangAcquire;

  @override
  Future<void> initialize() async {}

  @override
  Future<AccountInfo?> signIn() async {
    if (fail) throw StateError('boom');
    return account;
  }

  @override
  Future<void> signOut() async {}

  @override
  Future<String> acquireToken() {
    if (hangAcquire) return Completer<String>().future; // never completes
    return Future<String>.value('fake-token');
  }

  @override
  AccountInfo? currentAccount() => account;
}

Widget _host({
  required MsalAuthService auth,
  required AuthTokenStore tokens,
  Duration silentTimeout = const Duration(seconds: 30),
}) {
  return MaterialApp(
    theme: PlinkTheme.paper.copyWith(
      extensions: const <ThemeExtension<dynamic>>[
        PlinkProductAccent(Color(0xFF34357A)),
      ],
    ),
    home: LoginPage(tokens: tokens, auth: auth, silentTimeout: silentTimeout),
  );
}

void main() {
  testWidgets('renders the paper chrome, headline and primary action', (
    tester,
  ) async {
    tester.view.physicalSize = const Size(1200, 900);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);

    await tester.pumpWidget(_host(auth: _FakeAuth(), tokens: AuthTokenStore()));
    await tester.pumpAndSettle();

    // Identity rule + lockup carry the brand; the eyebrow is mono/uppercased.
    expect(find.byType(PlinkIdentityRule), findsOneWidget);
    expect(find.byType(AnchorLockup), findsOneWidget);
    expect(find.text('ANCHOR FOR TEACHERS'), findsOneWidget);

    // The one oversized Fraunces line, and the single primary action.
    expect(find.byKey(const Key('login-headline')), findsOneWidget);
    expect(
      find.widgetWithText(ElevatedButton, 'Sign in with Microsoft'),
      findsOneWidget,
    );

    // No overflow with the real fonts at this size.
    expect(tester.takeException(), isNull);
  });

  testWidgets('the primary action is the magenta spark', (tester) async {
    await tester.pumpWidget(_host(auth: _FakeAuth(), tokens: AuthTokenStore()));
    await tester.pumpAndSettle();

    final ButtonStyle? style = tester
        .widget<ElevatedButton>(find.byKey(const Key('sign-in')))
        .style;
    // The button inherits the DS spark from the theme (no inline override).
    expect(style?.backgroundColor, isNull);
    final ThemeData theme = PlinkTheme.paper;
    expect(theme.colorScheme.primary, PlinkColors.magenta);
  });

  testWidgets('successful sign-in stores the session', (tester) async {
    final tokens = AuthTokenStore();
    final auth = _FakeAuth(
      account: const AccountInfo(
        homeAccountId: 'home-1',
        username: 'teacher@school.example',
        displayName: 'Ms Teacher',
        department: null,
      ),
    );

    await tester.pumpWidget(_host(auth: auth, tokens: tokens));
    await tester.pumpAndSettle();

    await tester.tap(find.byKey(const Key('sign-in')));
    await tester.pumpAndSettle();

    expect(tokens.isAuthenticated, isTrue);
    expect(tokens.account?.displayName, 'Ms Teacher');
  });

  testWidgets('a failed sign-in surfaces an error and stays signed out', (
    tester,
  ) async {
    final tokens = AuthTokenStore();
    await tester.pumpWidget(_host(auth: _FakeAuth(fail: true), tokens: tokens));
    await tester.pumpAndSettle();

    await tester.tap(find.byKey(const Key('sign-in')));
    await tester.pumpAndSettle();

    expect(find.byKey(const Key('login-error')), findsOneWidget);
    expect(tokens.isAuthenticated, isFalse);
  });

  testWidgets(
    'a stalled silent acquisition times out into a retryable error (#303)',
    (tester) async {
      final tokens = AuthTokenStore();
      // signIn returns an account, but the silent token step never completes —
      // the day-old-cache hang. Without the timeout the button would spin
      // forever; with it, the page must settle into a clear, retryable error.
      final auth = _FakeAuth(
        account: const AccountInfo(
          homeAccountId: 'home-1',
          username: 'teacher@school.example',
          displayName: 'Ms Teacher',
          department: null,
        ),
        hangAcquire: true,
      );

      await tester.pumpWidget(
        _host(
          auth: auth,
          tokens: tokens,
          silentTimeout: const Duration(milliseconds: 200),
        ),
      );
      await tester.pumpAndSettle();

      await tester.tap(find.byKey(const Key('sign-in')));
      // While the silent step hangs, the button shows the busy spinner.
      await tester.pump();
      expect(find.byType(CircularProgressIndicator), findsOneWidget);
      expect(find.byKey(const Key('login-error')), findsNothing);

      // Advance past the bound: the stalled step times out into an error.
      await tester.pump(const Duration(milliseconds: 250));
      await tester.pumpAndSettle();

      expect(find.byKey(const Key('login-error')), findsOneWidget);
      expect(tokens.isAuthenticated, isFalse);
      // The spinner is gone and the button is re-enabled, so sign-in is
      // retryable rather than stuck.
      expect(find.byType(CircularProgressIndicator), findsNothing);
      final ElevatedButton button = tester.widget<ElevatedButton>(
        find.byKey(const Key('sign-in')),
      );
      expect(button.onPressed, isNotNull);
    },
  );
}
