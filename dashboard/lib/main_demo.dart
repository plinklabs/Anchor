// Demo entrypoint for the website screenshot generator (#250).
//
// Boots the *real* AnchorDashboard app — real router, real navigation, real
// Fraunces / Hanken / Space Mono fonts, real paper theme — but with auth
// BYPASSED and every backend call served from in-memory fakes seeded with the
// deterministic demo data in `demo/demo_data.dart`. No MSAL, no backend, no
// SignalR, no secrets: a pre-seeded AuthTokenStore gets the router past the
// /login redirect, fake API subclasses return fixed data, and a demo hub
// replays a fixed event set in place of the real SignalR feed.
//
// This mirrors the seam the integration tests already use to drive the app
// without a backend (see integration_test/live_session_test.dart). It is built
// as a separate web bundle and screenshotted route-by-route by
// `screenshots/generate-screenshots.mjs`. It is never the production entrypoint
// (that is `main.dart`).
//
//   flutter build web --target lib/main_demo.dart ...
//
// Run via `screenshots/generate-screenshots.mjs`, not by hand.

import 'dart:async';

import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/auth_token_store.dart';
import 'package:anchor_dashboard/api/bundles_api.dart';
import 'package:anchor_dashboard/api/classes_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/auth/msal_auth_service.dart';
import 'package:anchor_dashboard/main.dart';
import 'package:anchor_dashboard/realtime/session_hub_client.dart';
import 'package:flutter/material.dart';

import 'demo/demo_data.dart';

ApiClient _dummyClient() => ApiClient(
  baseUrl: Uri.parse('http://localhost'),
  tokenProvider: () async => null,
);

/// No-op auth. The router only checks `tokens.isAuthenticated`; nothing in the
/// demo flow calls back into the auth service, so this never has to do anything.
class _DemoAuth implements MsalAuthService {
  @override
  Future<void> initialize() async {}
  @override
  Future<AccountInfo?> signIn() async => null;
  @override
  Future<void> signOut() async {}
  @override
  Future<String> acquireToken() async => 'demo-token';
  @override
  AccountInfo? currentAccount() => null;
}

class _DemoSessions extends SessionsApi {
  _DemoSessions() : super(_dummyClient());

  @override
  Future<MeResponse> me() async => MeResponse(
    id: 't1',
    displayName: demoTeacherName,
    // Admin so the Bundles nav slot is exposed and the bundles editor renders
    // (the bundles screen is admin-only).
    role: 'Admin',
  );

  @override
  Future<List<ClassSummary>> classes() async => [
    ClassSummary(
      id: demoClassId,
      name: demoClassName,
      schoolYear: demoSchoolYear,
      schoolTag: demoSchoolTag,
      classCode: demoClassName,
    ),
  ];

  @override
  Future<List<ActiveSession>> activeSessions() async => [
    ActiveSession(
      id: demoSessionId,
      classId: demoClassId,
      startedAt: demoSessionStartedAt,
    ),
  ];

  @override
  Future<List<SessionHistoryEntry>> history({
    int limit = 50,
    int offset = 0,
  }) async => offset > 0 ? const [] : demoHistory();

  @override
  Future<SessionDetail> getSession(String sessionId) async =>
      sessionId == demoPastSessionId
      ? demoPastSessionDetail()
      : demoLiveSessionDetail();

  @override
  Future<StartSessionResponse> startSession(
    String classId, {
    List<String> bundleIds = const <String>[],
  }) async => StartSessionResponse(
    id: demoSessionId,
    classId: classId,
    joinCode: demoJoinCode,
    startedAt: demoSessionStartedAt,
  );

  @override
  Future<List<UnblockRequestSummary>> unblockRequests(String sessionId) async =>
      sessionId == demoPastSessionId ? const [] : demoPendingRequests();

  @override
  Future<List<SessionBundleInfo>> updateBundles(
    String sessionId,
    List<String> bundleIds,
  ) async => demoSessionBundles();

  @override
  Future<void> endSession(String sessionId) async {}

  @override
  Future<void> approveUnblock(String s, String u, String h) async {}

  @override
  Future<void> approveUnblockForClass(String s, String h) async {}
}

class _DemoBundles extends BundlesApi {
  _DemoBundles() : super(_dummyClient());

  @override
  Future<List<BundleSummary>> list({bool includeArchived = false}) async =>
      demoBundleSummaries();

  @override
  Future<BundleDetail> get(String id) async => demoBundleDetail();
}

class _DemoClasses extends ClassesApi {
  _DemoClasses() : super(_dummyClient());

  @override
  Future<ClassMembersResponse> members(String classId) async =>
      ClassMembersResponse(
        id: demoClassId,
        name: demoClassName,
        schoolYear: demoSchoolYear,
        schoolTag: demoSchoolTag,
        classCode: demoClassName,
        members: demoRoster(),
      );

  @override
  Future<List<String>> schools() async => const ['SSM', 'SJI'];
}

/// Stands in for the SignalR client: replays the fixed demo event set on
/// connect so the live session feed is populated rather than "Waiting for
/// events…", with no real hub.
class _DemoHub extends SessionHubClient {
  _DemoHub()
    : super(apiBaseUrl: Uri.parse('http://localhost'), tokenProvider: _noToken);

  static Future<String?> _noToken() async => null;

  final _ctrl = StreamController<SessionEvent>.broadcast();

  @override
  Stream<SessionEvent> get events => _ctrl.stream;

  @override
  Future<void> connect() async {}

  @override
  Future<void> joinSession(String sessionId, {String? joinCode}) async {
    // Replay the fixed feed once the page has subscribed. The page subscribes
    // to `events` immediately after this call returns, so a short delay (rather
    // than a microtask) guarantees the listener is attached before we emit —
    // a broadcast stream drops anything added while it has no listeners.
    Timer(const Duration(milliseconds: 50), () {
      if (_ctrl.isClosed) return;
      for (final e in demoLiveEvents()) {
        _ctrl.add(
          SessionEvent(
            kind: e.kind,
            payload: e.payload,
            at: demoSessionStartedAt,
          ),
        );
      }
    });
  }

  @override
  Future<void> leaveSession(String sessionId) async {}
  @override
  Future<void> disconnect() async {}
  @override
  Future<void> dispose() async => _ctrl.close();
}

void main() {
  final tokens = AuthTokenStore()
    ..setSession(
      token: 'demo-token',
      account: const AccountInfo(
        homeAccountId: 'home-demo',
        username: demoTeacherUpn,
        displayName: demoTeacherName,
        department: null,
      ),
    );

  runApp(
    AnchorDashboard(
      tokens: tokens,
      auth: _DemoAuth(),
      api: _dummyClient(),
      sessions: _DemoSessions(),
      bundles: _DemoBundles(),
      classes: _DemoClasses(),
      apiBaseUrl: Uri.parse('http://localhost'),
      hubClientFactory: ({required apiBaseUrl, required tokenProvider}) =>
          _DemoHub(),
    ),
  );
}
