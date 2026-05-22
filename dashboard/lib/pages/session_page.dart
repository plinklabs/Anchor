import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

class SessionPage extends StatelessWidget {
  const SessionPage({super.key, required this.sessionId});

  final String sessionId;

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(
        title: Text('Session $sessionId'),
        leading: IconButton(
          icon: const Icon(Icons.arrow_back),
          onPressed: () => context.go('/'),
        ),
      ),
      body: const Center(
        child: Text('Session view — to be implemented.'),
      ),
    );
  }
}
