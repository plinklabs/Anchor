import 'package:go_router/go_router.dart';

import 'api/auth_token_store.dart';
import 'pages/home_page.dart';
import 'pages/login_page.dart';
import 'pages/session_page.dart';

GoRouter buildRouter(AuthTokenStore tokens) {
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
      GoRoute(
        path: '/login',
        builder: (context, state) => LoginPage(tokens: tokens),
      ),
      GoRoute(
        path: '/',
        builder: (context, state) => HomePage(tokens: tokens),
      ),
      GoRoute(
        path: '/session/:id',
        builder: (context, state) =>
            SessionPage(sessionId: state.pathParameters['id']!),
      ),
    ],
  );
}
