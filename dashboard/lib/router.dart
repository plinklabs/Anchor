import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:plink_design_system/plink_design_system.dart';

import 'api/admins_api.dart';
import 'api/auth_token_store.dart';
import 'api/bundles_api.dart';
import 'api/classes_api.dart';
import 'api/schools_api.dart';
import 'api/sessions_api.dart';
import 'auth/msal_auth_service.dart';
import 'bundles/bundle_file_io.dart';
import 'realtime/session_hub_client.dart';
import 'pages/bundles_page.dart';
import 'pages/classes_page.dart';
import 'pages/history_page.dart';
import 'pages/home_page.dart';
import 'pages/login_page.dart';
import 'pages/manage_admins_page.dart';
import 'pages/manage_schools_page.dart';
import 'pages/past_session_page.dart';
import 'pages/session_page.dart';
import 'widgets/admin_shell.dart';
import 'widgets/app_shell.dart';

/// The `/login` query parameter that holds the page a signed-out visitor asked
/// for, so that signing in takes them there instead of Home (#379).
const String loginFromParameter = 'from';

/// The in-app location that [from] names, if it is safe to send a teacher there
/// after sign-in (#379), else null.
///
/// [from] comes from the URL (`/login?from=...`), so anyone can write it into
/// a link. Only a same-app path that [routes] knows passes: an absolute URL, a
/// scheme-relative `//host`, a backslash (browsers read `/\host` as `//host`),
/// a control character (browsers drop tabs and newlines, which can turn
/// `/<tab>/host` into `//host`), another scheme (`javascript:`), a relative
/// path, an unknown page, or `/login` itself all return null, so `/login`
/// can't be used as an open redirect. GoRouter would hand such a location to
/// the browser as the page URL. Dot segments are resolved and a fragment is
/// dropped.
String? safeReturnLocation(String? from, {required RouteConfiguration routes}) {
  if (from == null || !from.startsWith('/') || from.startsWith('//')) {
    return null;
  }
  if (from.contains(r'\') ||
      from.codeUnits.any((unit) => unit < 0x20 || unit == 0x7f)) {
    return null;
  }
  final Uri? uri = Uri.tryParse(from);
  if (uri == null || uri.hasScheme || uri.hasAuthority) return null;
  final Uri location = uri.removeFragment();
  if (!location.path.startsWith('/') || location.path.startsWith('//')) {
    return null;
  }
  if (location.path == '/login' || routes.findMatch(location).isError) {
    return null;
  }
  return location.toString();
}

/// Where the router sends a signed-out visit to [requested]: `/login`, with
/// the requested page in [loginFromParameter] when sign-in can return there
/// (#379). Home needs no `from`: sign-in lands there anyway.
String loginLocationFor(Uri requested, {required RouteConfiguration routes}) {
  final String? from = safeReturnLocation(requested.toString(), routes: routes);
  if (from == null || from == '/') return '/login';
  return Uri(
    path: '/login',
    queryParameters: <String, String>{loginFromParameter: from},
  ).toString();
}

GoRouter buildRouter({
  required AuthTokenStore tokens,
  required MsalAuthService auth,
  required SessionsApi sessions,
  required BundlesApi bundles,
  required ClassesApi classes,
  required AdminsApi admins,
  required SchoolsApi schools,
  required Uri apiBaseUrl,
  SessionHubClientFactory? hubClientFactory,
  BundleFileIo? bundleFileIo,
  Duration loginSilentTimeout = const Duration(seconds: 30),
}) {
  late final GoRouter router;
  router = GoRouter(
    refreshListenable: tokens,
    initialLocation: '/',
    redirect: (context, state) {
      final loggedIn = tokens.isAuthenticated;
      final goingToLogin = state.matchedLocation == '/login';
      // Signed out: to /login, carrying the page that was asked for (#379).
      // Sign-in is an MSAL popup (web/anchor_auth.js), so the app never
      // leaves this page and the `from` in the URL is still there when it
      // succeeds. A reload that can't restore the session (#302) lands here
      // too, with the page it was on.
      if (!loggedIn && !goingToLogin) {
        return loginLocationFor(state.uri, routes: router.configuration);
      }
      // Signed in on /login (sign-in just succeeded, or a session restored on
      // a reload of /login): to the page that was asked for if it is a safe,
      // known in-app location, else Home. A page this teacher may not open
      // still handles that itself: the admin area sends a non-admin Home, and
      // a session that isn't theirs shows its page's error (#369, #382).
      if (loggedIn && goingToLogin) {
        return safeReturnLocation(
              state.uri.queryParameters[loginFromParameter],
              routes: router.configuration,
            ) ??
            '/';
      }
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
            builder: (context, state) =>
                HomePage(tokens: tokens, sessions: sessions),
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
            builder: (context, state) =>
                ClassesPage(sessions: sessions, classes: classes),
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
                  fileIo: bundleFileIo,
                ),
              ),
              GoRoute(
                path: '/admin/admins',
                builder: (context, state) => ManageAdminsPage(admins: admins),
              ),
              GoRoute(
                path: '/admin/schools',
                builder: (context, state) =>
                    ManageSchoolsPage(schools: schools),
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
  return router;
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
  if (location.startsWith('/admin/admins')) return AdminSection.admins;
  if (location.startsWith('/admin/schools')) return AdminSection.schools;
  // Bundles is the default landing sub-page (bare `/admin` redirects here).
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
