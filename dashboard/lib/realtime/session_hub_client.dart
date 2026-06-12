import 'dart:async';

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
typedef SessionHubClientFactory = SessionHubClient Function({
  required Uri apiBaseUrl,
  required Future<String?> Function() tokenProvider,
});

class SessionHubClient {
  SessionHubClient({
    required Uri apiBaseUrl,
    required Future<String?> Function() tokenProvider,
  }) : _hubUrl = apiBaseUrl.resolve('hubs/session').toString(),
       _tokenProvider = tokenProvider;

  final String _hubUrl;
  final Future<String?> Function() _tokenProvider;

  HubConnection? _connection;
  final _events = StreamController<SessionEvent>.broadcast();

  Stream<SessionEvent> get events => _events.stream;

  Future<void> connect() async {
    if (_connection != null) return;
    final connection = HubConnectionBuilder()
        .withUrl(
          _hubUrl,
          HttpConnectionOptions(
            accessTokenFactory: () async => (await _tokenProvider()) ?? '',
            logging: (level, message) {},
          ),
        )
        .withAutomaticReconnect()
        .build();

    connection.on('SessionStarted', (args) {
      final payload =
          args != null && args.isNotEmpty && args.first is Map
              ? Map<String, dynamic>.from(args.first as Map)
              : <String, dynamic>{};
      _events.add(
        SessionEvent(kind: 'SessionStarted', payload: payload, at: DateTime.now()),
      );
    });

    connection.on('SessionEnded', (args) {
      final sessionId =
          args != null && args.isNotEmpty ? args.first?.toString() ?? '' : '';
      _events.add(
        SessionEvent(
          kind: 'SessionEnded',
          payload: {'sessionId': sessionId},
          at: DateTime.now(),
        ),
      );
    });

    connection.on('UnblockRequested', (args) {
      final payload =
          args != null && args.isNotEmpty && args.first is Map
              ? Map<String, dynamic>.from(args.first as Map)
              : <String, dynamic>{};
      _events.add(
        SessionEvent(kind: 'UnblockRequested', payload: payload, at: DateTime.now()),
      );
    });

    // Roster state signals (#100): a member joined/declined/left, or their
    // agent stopped/resumed reporting. The page re-fetches the detail on each
    // so the roster reflects server truth — same pattern as UnblockRequested.
    for (final kind in const ['ParticipantStateChanged', 'HeartbeatLost', 'AgentReconnected']) {
      connection.on(kind, (args) {
        final payload =
            args != null && args.isNotEmpty && args.first is Map
                ? Map<String, dynamic>.from(args.first as Map)
                : <String, dynamic>{};
        _events.add(
          SessionEvent(kind: kind, payload: payload, at: DateTime.now()),
        );
      });
    }

    await connection.start();
    _connection = connection;
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
  }
}
