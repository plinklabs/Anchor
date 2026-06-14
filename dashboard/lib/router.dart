import 'package:flutter/widgets.dart';
import 'package:go_router/go_router.dart';

import 'api/auth_token_store.dart';
import 'api/bundles_api.dart';
import 'api/classes_api.dart';
import 'api/sessions_api.dart';
import 'auth/msal_auth_service.dart';
import 'realtime/session_hub_client.dart';
import 'pages/bundles_page.dart';
import 'pages/classes_page.dart';
import 'pages/history_page.dart';
import 'pages/home_page.dart';
import 'pages/login_page.dart';
import 'pages/past_session_page.dart';
import 'pages/session_page.dart';
import 'widgets/app_shell.dart';

GoRouter buildRouter({
  required AuthTokenStore tokens,
  required MsalAuthService auth,
  required SessionsApi sessions,
  required BundlesApi bundles,
  required ClassesApi classes,
  required Uri apiBaseUrl,
  SessionHubClientFactory? hubClientFactory,
}) {
  return GoRouter(
    refreshListenable: tokens,
    initialLocation: '/',
    redirect: (context, state) {
      final loggedIn = tokens.isAuthenticated;
      final goingToLogin = state.matchedLocation == '/login';
      if (!loggedIn && !goingToLogin) return '/login';
      if (loggedIn && goingToLogin) return '/';
      return null;
    },
    routes: [
      // Login sits outside the shell — it has no nav and its own (AD2) chrome.
      GoRoute(
        path: '/login',
        builder: (context, state) =>
            LoginPage(tokens: tokens, auth: auth),
      ),
      // Every authenticated page shares the app scaffold / nav / app-bar (AD1).
      ShellRoute(
        builder: (context, state, child) => _AppShellHost(
          location: state.uri.path,
          tokens: tokens,
          auth: auth,
          sessions: sessions,
          child: child,
        ),
        routes: [
          GoRoute(
            path: '/',
            builder: (context, state) => HomePage(
              tokens: tokens,
              sessions: sessions,
            ),
          ),
          GoRoute(
            path: '/session/:id',
            builder: (context, state) => SessionPage(
              sessionId: state.pathParameters['id']!,
              tokens: tokens,
              sessions: sessions,
              bundles: bundles,
              apiBaseUrl: apiBaseUrl,
              hubClientFactory: hubClientFactory,
            ),
          ),
          GoRoute(
            path: '/classes',
            builder: (context, state) => ClassesPage(
              sessions: sessions,
              classes: classes,
            ),
          ),
          GoRoute(
            path: '/bundles',
            builder: (context, state) => BundlesPage(
              bundles: bundles,
              sessions: sessions,
            ),
          ),
          GoRoute(
            path: '/history',
            builder: (context, state) => HistoryPage(sessions: sessions),
          ),
          GoRoute(
            path: '/history/:id',
            builder: (context, state) => PastSessionPage(
              sessionId: state.pathParameters['id']!,
              sessions: sessions,
            ),
          ),
        ],
      ),
    ],
  );
}

AppSection _sectionFor(String location) {
  if (location.startsWith('/classes')) return AppSection.classes;
  if (location.startsWith('/bundles')) return AppSection.bundles;
  if (location.startsWith('/session')) return AppSection.session;
  if (location.startsWith('/history/')) return AppSection.pastSession;
  if (location.startsWith('/history')) return AppSection.history;
  return AppSection.home;
}

/// Connects the presentational [AppShell] to app state: resolves the admin role
/// (from `/me`, for the Bundles nav slot), surfaces the signed-in account, and
/// wires navigation + sign-out. Lives in the router so no page has to thread
/// `auth`/role just to render the shared chrome.
class _AppShellHost extends StatefulWidget {
  const _AppShellHost({
    required this.location,
    required this.child,
    required this.tokens,
    required this.auth,
    required this.sessions,
  });

  final String location;
  final Widget child;
  final AuthTokenStore tokens;
  final MsalAuthService auth;
  final SessionsApi sessions;

  @override
  State<_AppShellHost> createState() => _AppShellHostState();
}

class _AppShellHostState extends State<_AppShellHost> {
  bool _isAdmin = false;

  @override
  void initState() {
    super.initState();
    _loadRole();
  }

  Future<void> _loadRole() async {
    try {
      final me = await widget.sessions.me();
      if (!mounted || me.isAdmin == _isAdmin) return;
      setState(() => _isAdmin = me.isAdmin);
    } catch (_) {
      // Non-fatal: without /me the Bundles slot simply stays hidden.
    }
  }

  Future<void> _signOut() async {
    try {
      await widget.auth.signOut();
    } finally {
      widget.tokens.clear();
      if (mounted) context.go('/login');
    }
  }

  @override
  Widget build(BuildContext context) {
    return AppShell(
      section: _sectionFor(widget.location),
      isAdmin: _isAdmin,
      accountName: widget.tokens.account?.displayName,
      onSignOut: _signOut,
      onNavigate: (location) => context.go(location),
      child: widget.child,
    );
  }
}
