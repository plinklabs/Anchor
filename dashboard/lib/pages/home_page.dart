import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

import '../api/auth_token_store.dart';

class HomePage extends StatelessWidget {
  const HomePage({super.key, required this.tokens});

  final AuthTokenStore tokens;

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(
        title: const Text('Anchor'),
        actions: [
          IconButton(
            tooltip: 'Sign out',
            icon: const Icon(Icons.logout),
            onPressed: () {
              tokens.clear();
              context.go('/login');
            },
          ),
        ],
      ),
      body: Center(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            const Text('No active session.'),
            const SizedBox(height: 16),
            FilledButton(
              onPressed: () => context.go('/session/demo'),
              child: const Text('Open demo session'),
            ),
          ],
        ),
      ),
    );
  }
}
