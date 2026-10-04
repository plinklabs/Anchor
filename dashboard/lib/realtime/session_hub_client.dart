import 'dart:async';
import 'dart:math' as math;

import 'package:flutter/foundation.dart';
import 'package:http/http.dart' as http;
import 'package:signalr_core/signalr_core.dart';

class SessionEvent {
  SessionEvent({required this.kind, required this.payload, required this.at});
  final String kind;
  final Map<String, dynamic> payload;
  final DateTime at;
}

/// How [SessionPage] obtains its live feed. Defaults to `SessionHubClient.new`
/// (the real SignalR client); an integration test injects a factory that
/// returns a stubbed feed it can push events through, since the dashboard is
/// MSAL-only and can't be dev-impersonated to drive a real hub (#132).
typedef SessionHubClientFactory =
    SessionHubClient Function({
      required Uri apiBaseUrl,
      required Future<String?> Function() tokenProvider,
    });

/// How the live page's connection retries after it drops (#370): it never
/// gives up, and it backs off from 1 s, doubling, to at most 30 s between
/// attempts. That is the agent's SignalRBackoffPolicy with its default 30 s
/// cap. SignalR's default policy retries at 0, 2, 10 and 30 s and then closes
/// the connection for good, which froze the live page after about 42 s
/// offline (a backend restart, a dropped network, a sleeping laptop).
class SessionHubRetryPolicy implements RetryPolicy {
  const SessionHubRetryPolicy();

  static const Duration maxDelay = Duration(seconds: 30);

  @override
  int nextRetryDelayInMilliseconds(RetryContext retryContext) {
    final attempt = math.min(retryContext.previousRetryCount ?? 0, 10);
    return math.min(maxDelay.inMilliseconds, 1000 << attempt);
  }
}

/// Whether the live feed is getting through (#370), for the page's
/// connection indicator.
enum SessionHubLinkState {
  /// Connected: broadcasts arrive.
  connected,

  /// The connection dropped and is being retried. Nothing arrives until it's
  /// back, and then [SessionHubClient.reconnected] fires.
  reconnecting,

  /// The connection is closed and nothing retries it: its first start failed,
  /// or the server closed it. [SessionHubClient.restart] starts it again.
  disconnected,
}

class SessionHubClient {
  SessionHubClient({
    required Uri apiBaseUrl,
    required Future<String?> Function() tokenProvider,
    @visibleForTesting http.BaseClient? httpClient,
  }) : _hubUrl = apiBaseUrl.resolve('hubs/session').toString(),
       _tokenProvider = tokenProvider,
       _httpClient = httpClient;

  final String _hubUrl;
  final Future<String?> Function() _tokenProvider;

  /// What SignalR negotiates and opens its WebSocket through; null for its
  /// default. A test injects one to take the network down (#370).
  final http.BaseClient? _httpClient;

  HubConnection? _connection;
  final _events = StreamController<SessionEvent>.broadcast();
  final _reconnected = StreamController<void>.broadcast();
  final _linkState = StreamController<SessionHubLinkState>.broadcast();

  Stream<SessionEvent> get events => _events.stream;

  /// Each change in whether the live feed gets through (#370): it dropped and
  /// is reconnecting, it closed, or it's back.
  Stream<SessionHubLinkState> get linkState => _linkState.stream;

  /// Fires each time the connection comes back after a drop (#365), however
  /// long it was down (#370), and after a [restart]. A reconnect is a new hub
  /// connection: the hub puts it back in the user's group (OnConnectedAsync)
  /// but in no session group, so a caller that joined a session has to call
  /// [joinSession] again — and catch up on whatever was broadcast while it
  /// was offline.
  Stream<void> get reconnected => _reconnected.stream;

  Future<void> connect() async {
    if (_connection != null) return;
    final connection = HubConnectionBuilder()
        .withUrl(
          _hubUrl,
          HttpConnectionOptions(
            accessTokenFactory: () async => (await _tokenProvider()) ?? '',
            logging: (level, message) {},
            client: _httpClient,
          ),
        )
        .withAutomaticReconnect(const SessionHubRetryPolicy())
        .build();

    connection.on('SessionStarted', (args) {
      final payload = args != null && args.isNotEmpty && args.first is Map
          ? Map<String, dynamic>.from(args.first as Map)
          : <String, dynamic>{};
      _events.add(
        SessionEvent(
          kind: 'SessionStarted',
          payload: payload,
          at: DateTime.now(),
        ),
      );
    });

    connection.on('SessionEnded', (args) {
      final sessionId = args != null && args.isNotEmpty
          ? args.first?.toString() ?? ''
          : '';
      _events.add(
        SessionEvent(
          kind: 'SessionEnded',
          payload: {'sessionId': sessionId},
          at: DateTime.now(),
        ),
      );
    });

    connection.on('UnblockRequested', (args) {
      final payload = args != null && args.isNotEmpty && args.first is Map
          ? Map<String, dynamic>.from(args.first as Map)
          : <String, dynamic>{};
      _events.add(
        SessionEvent(
          kind: 'UnblockRequested',
          payload: payload,
          at: DateTime.now(),
        ),
      );
    });

    // Roster state signals (#100, #105): a member joined/declined/left, their
    // agent stopped/resumed reporting, or a tamper attempt was detected. The
    // page re-fetches the detail on each so the roster reflects server truth —
    // same pattern as UnblockRequested.
    for (final kind in const [
      'ParticipantStateChanged',
      'HeartbeatLost',
      'AgentReconnected',
      'TamperDetected',
    ]) {
      connection.on(kind, (args) {
        final payload = args != null && args.isNotEmpty && args.first is Map
            ? Map<String, dynamic>.from(args.first as Map)
            : <String, dynamic>{};
        _events.add(
          SessionEvent(kind: kind, payload: payload, at: DateTime.now()),
        );
      });
    }

    connection.onreconnecting((_) {
      _setLinkState(SessionHubLinkState.reconnecting);
    });
    connection.onreconnected((_) {
      _setLinkState(SessionHubLinkState.connected);
      if (!_reconnected.isClosed) _reconnected.add(null);
    });
    connection.onclose((_) {
      // Not when [disconnect] stopped it on purpose.
      if (identical(_connection, connection)) {
        _setLinkState(SessionHubLinkState.disconnected);
      }
    });

    // Kept even if the first start fails, so [restart] can try again.
    _connection = connection;
    try {
      await connection.start();
    } catch (_) {
      _setLinkState(SessionHubLinkState.disconnected);
      rethrow;
    }
    _setLinkState(SessionHubLinkState.connected);
  }

  /// Starts the connection again if it's closed (#370), e.g. when the teacher
  /// comes back to the page or asks to reconnect. Does nothing while it's
  /// connected or still reconnecting on its own. Once it's back,
  /// [reconnected] fires, as after an automatic reconnect.
  Future<void> restart() async {
    final connection = _connection;
    if (connection == null ||
        connection.state != HubConnectionState.disconnected) {
      return;
    }
    _setLinkState(SessionHubLinkState.reconnecting);
    try {
      await connection.start();
    } catch (_) {
      if (identical(_connection, connection)) {
        _setLinkState(SessionHubLinkState.disconnected);
      }
      return;
    }
    if (!identical(_connection, connection)) return; // disconnected meanwhile
    _setLinkState(SessionHubLinkState.connected);
    if (!_reconnected.isClosed) _reconnected.add(null);
  }

  void _setLinkState(SessionHubLinkState state) {
    if (!_linkState.isClosed) _linkState.add(state);
  }

  Future<void> joinSession(String sessionId, {String? joinCode}) async {
    final connection = _connection;
    if (connection == null) throw StateError('SignalR not connected');
    await connection.invoke(
      'JoinSession',
      args: [
        {'sessionId': sessionId, 'joinCode': joinCode},
      ],
    );
  }

  Future<void> leaveSession(String sessionId) async {
    final connection = _connection;
    if (connection == null) return;
    await connection.invoke('LeaveSession', args: [sessionId]);
  }

  Future<void> disconnect() async {
    final connection = _connection;
    _connection = null;
    if (connection != null) {
      await connection.stop();
    }
  }

  Future<void> dispose() async {
    await disconnect();
    await _events.close();
    await _reconnected.close();
    await _linkState.close();
  }
}
