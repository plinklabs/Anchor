import 'dart:async';

import 'package:flutter/material.dart';
import 'package:plink_design_system/plink_design_system.dart';

import '../api/auth_token_store.dart';
import '../auth/msal_auth_service.dart';
import '../widgets/anchor_mark.dart';

/// The dashboard sign-in page (AD2, #167) — paper treatment + brand voice.
///
/// Login sits outside the app shell (no nav, no account chrome yet), so it
/// carries its own light chrome: the indigo identity rule pinned to the top
/// like the shell, then a flush-left editorial column — lockup, mono eyebrow,
/// one oversized Fraunces line, and the single primary (magenta) action,
/// Microsoft sign-in. Everything reads on the warm paper surface the teacher
/// dashboard wears for its whole life (ANCHOR_BRAND.md §6).
class LoginPage extends StatefulWidget {
  const LoginPage({
    super.key,
    required this.tokens,
    required this.auth,
    this.silentTimeout = const Duration(seconds: 30),
  });

  final AuthTokenStore tokens;
  final MsalAuthService auth;

  /// Upper bound on the *non-interactive* steps of sign-in: MSAL init and the
  /// silent token acquisition. A day-old cached session can leave the silent
  /// path stalled on a hidden-iframe renewal that never resolves (#303); this
  /// bound turns that infinite spinner into a clear, retryable error. The
  /// interactive popup ([MsalAuthService.signIn]) is deliberately *not* bounded
  /// — a user may legitimately take a while picking an account or completing
  /// MFA. Overridable so tests can drive the timeout without a real wait.
  final Duration silentTimeout;

  @override
  State<LoginPage> createState() => _LoginPageState();
}

class _LoginPageState extends State<LoginPage> {
  bool _busy = false;
  String? _error;

  Future<void> _signIn() async {
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      await widget.auth.initialize().timeout(widget.silentTimeout);
      // Interactive: unbounded on purpose — the popup waits on the user.
      final account = await widget.auth.signIn();
      if (account == null) {
        throw StateError('Sign-in returned no account');
      }
      final token =
          await widget.auth.acquireToken().timeout(widget.silentTimeout);
      widget.tokens.setSession(token: token, account: account);
    } on TimeoutException {
      if (!mounted) return;
      setState(() => _error =
          'Signing in is taking longer than expected. Please try again.');
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = e.toString());
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final TextTheme text = Theme.of(context).textTheme;

    return Scaffold(
      backgroundColor: PlinkColors.paper,
      body: SafeArea(
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: <Widget>[
            // The one per-product identity element — indigo, decorative —
            // matching the authenticated shell so login feels like Anchor.
            const ExcludeSemantics(child: PlinkIdentityRule()),
            Expanded(
              child: Center(
                child: SingleChildScrollView(
                  padding: const EdgeInsets.symmetric(
                    horizontal: PlinkSpacing.s6,
                    vertical: PlinkSpacing.s7,
                  ),
                  child: ConstrainedBox(
                    // Wide enough that the oversized Fraunces line sits on one
                    // line on a desktop window; it still wraps (never clips)
                    // when the viewport is narrower.
                    constraints: const BoxConstraints(maxWidth: 760),
                    child: Column(
                      mainAxisSize: MainAxisSize.min,
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: <Widget>[
                        const AnchorLockup(height: 30),
                        const SizedBox(height: PlinkSpacing.s8),
                        const Eyebrow('Anchor for teachers'),
                        const SizedBox(height: PlinkSpacing.s4),
                        // The one oversized Fraunces line, flush-left.
                        Text(
                          'Ready when your class is.',
                          key: const Key('login-headline'),
                          style: text.displayMedium,
                        ),
                        const SizedBox(height: PlinkSpacing.s5),
                        Text(
                          'Sign in with your school account to start a focus '
                          'session for a class.',
                          style: text.bodyLarge?.copyWith(
                            color: PlinkColors.ink60,
                          ),
                        ),
                        const SizedBox(height: PlinkSpacing.s7),
                        // The single primary (magenta) action. The DS theme
                        // paints ElevatedButton in the spark, so it stays the
                        // one bright point on the page.
                        ElevatedButton(
                          key: const Key('sign-in'),
                          onPressed: _busy ? null : _signIn,
                          child: _busy
                              ? const SizedBox(
                                  width: 16,
                                  height: 16,
                                  child: CircularProgressIndicator(
                                    strokeWidth: 2,
                                    color: PlinkColors.onInk,
                                  ),
                                )
                              : const Text('Sign in with Microsoft'),
                        ),
                        if (_error != null) ...<Widget>[
                          const SizedBox(height: PlinkSpacing.s4),
                          Text(
                            _error!,
                            key: const Key('login-error'),
                            style: text.bodyMedium?.copyWith(
                              color: Theme.of(context).colorScheme.error,
                            ),
                          ),
                        ],
                      ],
                    ),
                  ),
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}
