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
      GoRoute(
        path: '/login',
        builder: (context, state) =>
            LoginPage(tokens: tokens, auth: auth),
      ),
      GoRoute(
        path: '/',
        builder: (context, state) => HomePage(
          tokens: tokens,
          auth: auth,
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
  );
}
