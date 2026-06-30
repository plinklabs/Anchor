import 'package:flutter/material.dart';
import 'package:plink_design_system/plink_design_system.dart';

import 'api/admins_api.dart';
import 'api/api_client.dart';
import 'api/auth_token_store.dart';
import 'api/bundles_api.dart';
import 'api/classes_api.dart';
import 'api/schools_api.dart';
import 'api/sessions_api.dart';
import 'auth/msal_auth_service.dart';
import 'auth/msal_config.dart';
import 'bundles/bundle_file_io.dart';
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
  final admins = AdminsApi(api);
  final schools = SchoolsApi(api);

  runApp(
    AnchorDashboard(
      tokens: tokens,
      auth: auth,
      api: api,
      sessions: sessions,
      bundles: bundles,
      classes: classes,
      admins: admins,
      schools: schools,
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
    this.admins,
    this.schools,
    this.hubClientFactory,
    this.bundleFileIo,
    this.loginSilentTimeout = const Duration(seconds: 30),
  });

  final AuthTokenStore tokens;
  final MsalAuthService auth;
  final ApiClient api;
  final SessionsApi sessions;
  final BundlesApi bundles;
  final ClassesApi classes;
  final Uri apiBaseUrl;

  /// Client for the admin-management surface (#300). Optional so existing call
  /// sites (tests) need not supply it — defaults to one built from [api].
  final AdminsApi? admins;

  /// Client for the admin schools surface (#301). Optional so existing call
  /// sites (tests) need not supply it — defaults to one built from [api].
  final SchoolsApi? schools;

  /// Overrides the live-feed builder for the session view (#132). Null in
  /// production; an integration test injects a stubbed feed to drive the real
  /// app without a real SignalR hub.
  final SessionHubClientFactory? hubClientFactory;

  /// Overrides the bundle import/export file-IO seam (#304). Null in
  /// production (the real `package:web` implementation is built on demand); an
  /// integration test injects a fake so the flow runs without an OS file
  /// dialog.
  final BundleFileIo? bundleFileIo;

  /// Upper bound on the non-interactive steps of sign-in, forwarded to
  /// [LoginPage.silentTimeout] (#303). Defaults to the production value; an
  /// integration test shortens it to drive the timeout without a real wait.
  final Duration loginSilentTimeout;

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
    admins: widget.admins ?? AdminsApi(widget.api),
    schools: widget.schools ?? SchoolsApi(widget.api),
    apiBaseUrl: widget.apiBaseUrl,
    hubClientFactory: widget.hubClientFactory,
    bundleFileIo: widget.bundleFileIo,
    loginSilentTimeout: widget.loginSilentTimeout,
  );

  // Rehydrate the session from MSAL before the router runs, so a reload (or a
  // reopened tab) with a still-valid cached session lands straight on the app
  // instead of forcing a re-login (#302). The router only knows you're signed
  // in via the in-memory [AuthTokenStore], which starts empty on every fresh
  // page load — but MSAL still holds the session in localStorage. The build
  // gates on this future so we never flash /login before the check resolves.
  late final Future<void> _restored = _restoreSession();

  Future<void> _restoreSession() async {
    try {
      await widget.auth.initialize().timeout(widget.loginSilentTimeout);
      final account = widget.auth.currentAccount();
      // No cached account → genuinely signed out; let the router go to /login.
      if (account == null) return;
      // Silent only: a boot-time interactive popup would be jarring (and is
      // blocked without a user gesture). If it can't be acquired silently, we
      // fall through to /login for an explicit sign-in.
      final token = await widget.auth.acquireTokenSilent().timeout(
        widget.loginSilentTimeout,
      );
      widget.tokens.setSession(token: token, account: account);
    } catch (_) {
      // No MSAL on this platform, no restorable account, an expired session
      // that needs interaction, or a stalled silent renewal — all mean "not
      // restorable". Leave the store empty; the router sends the user to
      // /login.
    }
  }

  // Teacher-facing surface: paper (light) only — never the ink theme
  // (ANCHOR_BRAND.md §3). The one Anchor identity layered on the Plink
  // foundations is the deep-indigo product accent (#34357A on paper),
  // reserved for the mark/identity rule; magenta stays the spark.
  ThemeData get _theme => PlinkTheme.paper.copyWith(
    extensions: const <ThemeExtension<dynamic>>[
      PlinkProductAccent(Color(0xFF34357A)),
    ],
  );

  @override
  Widget build(BuildContext context) {
    return FutureBuilder<void>(
      future: _restored,
      builder: (context, snapshot) {
        if (snapshot.connectionState != ConnectionState.done) {
          // Quiet boot gate while we check for a restorable session, so a
          // reload never flashes the login page before rehydration resolves.
          return MaterialApp(
            title: 'Anchor',
            theme: _theme,
            home: const Scaffold(
              backgroundColor: PlinkColors.paper,
              body: Center(child: CircularProgressIndicator()),
            ),
          );
        }
        return MaterialApp.router(
          title: 'Anchor',
          theme: _theme,
          routerConfig: _router,
        );
      },
    );
  }
}
