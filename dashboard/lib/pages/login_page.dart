import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

import '../api/auth_token_store.dart';

class LoginPage extends StatelessWidget {
  const LoginPage({super.key, required this.tokens});

  final AuthTokenStore tokens;

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: const Text('Anchor — Sign in')),
      body: Center(
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 360),
          child: Padding(
            padding: const EdgeInsets.all(24),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                const Text(
                  'Sign in with your school account to start a focus session.',
                  textAlign: TextAlign.center,
                ),
                const SizedBox(height: 24),
                FilledButton(
                  onPressed: () {
                    tokens.setToken('dev-stub-token');
                    context.go('/');
                  },
                  child: const Text('Sign in (stub)'),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}
