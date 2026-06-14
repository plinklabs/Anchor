import 'package:flutter/material.dart';
import 'package:plink_design_system/plink_design_system.dart';

import 'api/api_client.dart';
import 'api/auth_token_store.dart';
import 'api/bundles_api.dart';
import 'api/classes_api.dart';
import 'api/sessions_api.dart';
import 'auth/msal_auth_service.dart';
import 'auth/msal_config.dart';
import 'realtime/session_hub_client.dart';
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
  final bundles = BundlesApi(api);
  final classes = ClassesApi(api);

  runApp(
    AnchorDashboard(
      tokens: tokens,
      auth: auth,
      api: api,
      sessions: sessions,
      bundles: bundles,
      classes: classes,
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
    required this.bundles,
    required this.classes,
    required this.apiBaseUrl,
    this.hubClientFactory,
  });

  final AuthTokenStore tokens;
  final MsalAuthService auth;
  final ApiClient api;
  final SessionsApi sessions;
  final BundlesApi bundles;
  final ClassesApi classes;
  final Uri apiBaseUrl;

  /// Overrides the live-feed builder for the session view (#132). Null in
  /// production; an integration test injects a stubbed feed to drive the real
  /// app without a real SignalR hub.
  final SessionHubClientFactory? hubClientFactory;

  @override
  State<AnchorDashboard> createState() => _AnchorDashboardState();
}

class _AnchorDashboardState extends State<AnchorDashboard> {
  late final _router = buildRouter(
    tokens: widget.tokens,
    auth: widget.auth,
    sessions: widget.sessions,
    bundles: widget.bundles,
    classes: widget.classes,
    apiBaseUrl: widget.apiBaseUrl,
    hubClientFactory: widget.hubClientFactory,
  );

  @override
  Widget build(BuildContext context) {
    return MaterialApp.router(
      title: 'Anchor',
      // Teacher-facing surface: paper (light) only — never the ink theme
      // (ANCHOR_BRAND.md §3). The one Anchor identity layered on the Plink
      // foundations is the deep-indigo product accent (#34357A on paper),
      // reserved for the mark/identity rule; magenta stays the spark.
      theme: PlinkTheme.paper.copyWith(
        extensions: const <ThemeExtension<dynamic>>[
          PlinkProductAccent(Color(0xFF34357A)),
        ],
      ),
      routerConfig: _router,
    );
  }
}
