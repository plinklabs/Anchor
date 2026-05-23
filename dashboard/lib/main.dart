import 'package:flutter/material.dart';

import 'api/api_client.dart';
import 'api/auth_token_store.dart';
import 'api/sessions_api.dart';
import 'auth/msal_auth_service.dart';
import 'auth/msal_config.dart';
import 'router.dart';

void main() {
  const apiBase = String.fromEnvironment(
    'API_BASE_URL',
    defaultValue: 'http://localhost:5276',
  );

  final tokens = AuthTokenStore();
  final auth = MsalAuthService(MsalConfig.fromEnvironment());
  final api = ApiClient(
    baseUrl: Uri.parse(apiBase),
    tokenProvider: () async {
      if (!tokens.isAuthenticated) return null;
      final token = await auth.acquireToken();
      tokens.setToken(token);
      return token;
    },
  );
  final sessions = SessionsApi(api);

  runApp(
    AnchorDashboard(
      tokens: tokens,
      auth: auth,
      api: api,
      sessions: sessions,
      apiBaseUrl: Uri.parse(apiBase),
    ),
  );
}

class AnchorDashboard extends StatefulWidget {
  const AnchorDashboard({
    super.key,
    required this.tokens,
    required this.auth,
    required this.api,
    required this.sessions,
    required this.apiBaseUrl,
  });

  final AuthTokenStore tokens;
  final MsalAuthService auth;
  final ApiClient api;
  final SessionsApi sessions;
  final Uri apiBaseUrl;

  @override
  State<AnchorDashboard> createState() => _AnchorDashboardState();
}

class _AnchorDashboardState extends State<AnchorDashboard> {
  late final _router = buildRouter(
    tokens: widget.tokens,
    auth: widget.auth,
    sessions: widget.sessions,
    apiBaseUrl: widget.apiBaseUrl,
  );

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
