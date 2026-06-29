import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:plink_design_system/plink_design_system.dart';

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
import 'widgets/admin_shell.dart';
import 'widgets/app_shell.dart';

GoRouter buildRouter({
  required AuthTokenStore tokens,
  required MsalAuthService auth,
  required SessionsApi sessions,
  required BundlesApi bundles,
  required ClassesApi classes,
  required Uri apiBaseUrl,
  SessionHubClientFactory? hubClientFactory,
  Duration loginSilentTimeout = const Duration(seconds: 30),
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
        builder: (context, state) => LoginPage(
          tokens: tokens,
          auth: auth,
          silentTimeout: loginSilentTimeout,
        ),
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
          // Old standalone Bundles location — kept as a redirect so existing
          // links/bookmarks land on its new home under the Admin area (#299).
          GoRoute(
            path: '/bundles',
            redirect: (context, state) => '/admin/bundles',
          ),
          // Bare /admin has no page of its own — open the first sub-tab.
          GoRoute(
            path: '/admin',
            redirect: (context, state) => '/admin/bundles',
          ),
          // The admin area: a left vertical sub-nav (AdminShell) wrapping each
          // admin sub-page. Gated on `isAdmin` by _AdminShellHost, which
          // redirects non-admins away (consistent with the hidden Admin tab).
          ShellRoute(
            builder: (context, state, child) => _AdminShellHost(
              location: state.uri.path,
              tokens: tokens,
              sessions: sessions,
              child: child,
            ),
            routes: [
              GoRoute(
                path: '/admin/bundles',
                builder: (context, state) => BundlesPage(
                  bundles: bundles,
                  sessions: sessions,
                ),
              ),
            ],
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
  // `/bundles` only exists as a redirect to `/admin/bundles`, but map it too so
  // the Admin tab reads active during the redirect frame.
  if (location.startsWith('/admin') || location.startsWith('/bundles')) {
    return AppSection.admin;
  }
  if (location.startsWith('/session')) return AppSection.session;
  if (location.startsWith('/history/')) return AppSection.pastSession;
  if (location.startsWith('/history')) return AppSection.history;
  return AppSection.home;
}

/// Maps an `/admin/...` location to its sub-page for the [AdminShell] rail.
AdminSection _adminSectionFor(String location) {
  // Only Bundles today; new admin sub-pages add their own branch here.
  return AdminSection.bundles;
}

/// Connects the presentational [AppShell] to app state: resolves the admin role
/// (from `/me`, for the Admin nav slot), surfaces the signed-in account, and
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
      // Non-fatal: without /me the Admin slot simply stays hidden.
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

/// Gates and frames the admin area: resolves the admin role (from `/me`) and,
/// once it knows, either wraps the routed admin sub-page in [AdminShell]'s
/// left vertical sub-nav (admins) or redirects away to Home (non-admins) — the
/// route-level half of the same gating that hides the Admin tab. Sits inside
/// the [AppShell] shell route, so the app-bar/eyebrow chrome stays put.
class _AdminShellHost extends StatefulWidget {
  const _AdminShellHost({
    required this.location,
    required this.child,
    required this.tokens,
    required this.sessions,
  });

  final String location;
  final Widget child;
  final AuthTokenStore tokens;
  final SessionsApi sessions;

  @override
  State<_AdminShellHost> createState() => _AdminShellHostState();
}

class _AdminShellHostState extends State<_AdminShellHost> {
  // null while /me is in flight — we hold the content back until we know, so a
  // non-admin never sees an admin page flash before the redirect.
  bool? _isAdmin;

  @override
  void initState() {
    super.initState();
    _loadRole();
  }

  Future<void> _loadRole() async {
    bool isAdmin = false;
    try {
      final me = await widget.sessions.me();
      isAdmin = me.isAdmin;
    } catch (_) {
      // Treat an unresolvable role as non-admin: fail closed, redirect away.
    }
    if (!mounted) return;
    setState(() => _isAdmin = isAdmin);
    if (!isAdmin) context.go('/');
  }

  @override
  Widget build(BuildContext context) {
    // Still resolving, or a non-admin about to be redirected: show a quiet
    // placeholder rather than the admin content.
    if (_isAdmin != true) {
      return const Scaffold(
        backgroundColor: PlinkColors.paper,
        body: Center(child: CircularProgressIndicator()),
      );
    }
    return AdminShell(
      section: _adminSectionFor(widget.location),
      onNavigate: (location) => context.go(location),
      child: widget.child,
    );
  }
}
