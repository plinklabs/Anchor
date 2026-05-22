import 'package:flutter/material.dart';

import 'api/api_client.dart';
import 'api/auth_token_store.dart';
import 'router.dart';

void main() {
  const apiBase = String.fromEnvironment(
    'API_BASE_URL',
    defaultValue: 'http://localhost:5000',
  );

  final tokens = AuthTokenStore();
  final api = ApiClient(baseUrl: Uri.parse(apiBase), tokens: tokens);

  runApp(AnchorDashboard(tokens: tokens, api: api));
}

class AnchorDashboard extends StatefulWidget {
  const AnchorDashboard({super.key, required this.tokens, required this.api});

  final AuthTokenStore tokens;
  final ApiClient api;

  @override
  State<AnchorDashboard> createState() => _AnchorDashboardState();
}

class _AnchorDashboardState extends State<AnchorDashboard> {
  late final _router = buildRouter(widget.tokens);

  @override
  Widget build(BuildContext context) {
    return MaterialApp.router(
      title: 'Anchor',
      theme: ThemeData(
        colorScheme: ColorScheme.fromSeed(seedColor: Colors.indigo),
        useMaterial3: true,
      ),
      routerConfig: _router,
    );
  }
}
