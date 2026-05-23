import 'package:go_router/go_router.dart';

import 'api/auth_token_store.dart';
import 'api/sessions_api.dart';
import 'auth/msal_auth_service.dart';
import 'pages/home_page.dart';
import 'pages/login_page.dart';
import 'pages/session_page.dart';

GoRouter buildRouter({
  required AuthTokenStore tokens,
  required MsalAuthService auth,
  required SessionsApi sessions,
  required Uri apiBaseUrl,
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
      GoRoute(
        path: '/login',
        builder: (context, state) =>
            LoginPage(tokens: tokens, auth: auth),
      ),
      GoRoute(
        path: '/',
        builder: (context, state) =>
            HomePage(tokens: tokens, auth: auth, sessions: sessions),
      ),
      GoRoute(
        path: '/session/:id',
        builder: (context, state) => SessionPage(
          sessionId: state.pathParameters['id']!,
          tokens: tokens,
          sessions: sessions,
          apiBaseUrl: apiBaseUrl,
        ),
      ),
    ],
  );
}
