import 'dart:convert';
import 'dart:io';

import 'package:anchor_dashboard/realtime/session_hub_client.dart';
import 'package:flutter_test/flutter_test.dart';

// #365: the real SessionHubClient against an in-process SignalR hub. SignalR
// keeps no group membership across a reconnect, so the client has to tell its
// page when the connection comes back; the page then joins its session again.

const _sessionId = 'aaaaaaaa-0000-0000-0000-000000000001';
const _rs = '\u001e';

/// A minimal SignalR hub over WebSockets and the JSON protocol: it answers
/// negotiate and the handshake, adds a connection to the session group on
/// JoinSession, and broadcasts to that group. Like the real hub, a new
/// connection (a reconnect included) is in no session group.
class _FakeHub {
  late final HttpServer _server;
  final _sockets = <WebSocket>{};
  final _sessionGroup = <WebSocket>{};
  int _nextConnection = 0;
  int joinCalls = 0;

  Uri get baseUrl => Uri.parse('http://127.0.0.1:${_server.port}/');

  Future<void> start() async {
    _server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
    _server.listen(_handle);
  }

  Future<void> _handle(HttpRequest request) async {
    if (request.uri.path == '/hubs/session/negotiate') {
      final id = 'connection-${_nextConnection++}';
      request.response.headers.contentType = ContentType.json;
      request.response.write(
        jsonEncode({
          'negotiateVersion': 1,
          'connectionId': id,
          'connectionToken': id,
          'availableTransports': [
            {
              'transport': 'WebSockets',
              'transferFormats': ['Text', 'Binary'],
            },
          ],
        }),
      );
      await request.response.close();
      return;
    }
    if (request.uri.path == '/hubs/session' &&
        WebSocketTransformer.isUpgradeRequest(request)) {
      final socket = await WebSocketTransformer.upgrade(request);
      _sockets.add(socket);
      socket.listen(
        (frame) => _receive(
          socket,
          frame is String ? frame : utf8.decode(frame as List<int>),
        ),
        onDone: () {
          _sockets.remove(socket);
          _sessionGroup.remove(socket);
        },
      );
      return;
    }
    request.response.statusCode = HttpStatus.notFound;
    await request.response.close();
  }

  void _receive(WebSocket socket, String frame) {
    for (final raw in frame.split(_rs).where((m) => m.isNotEmpty)) {
      final message = jsonDecode(raw) as Map<String, dynamic>;
      if (message.containsKey('protocol')) {
        socket.add('{}$_rs'); // handshake accepted
      } else if (message['type'] == 1 && message['target'] == 'JoinSession') {
        joinCalls++;
        _sessionGroup.add(socket);
        socket.add(
          '${jsonEncode({
            'type': 3,
            'invocationId': message['invocationId'],
            'result': {'sessionId': _sessionId, 'userId': 'teacher'},
          })}$_rs',
        );
      }
    }
  }

  void broadcastToSessionGroup(String target, Map<String, dynamic> payload) {
    for (final socket in _sessionGroup) {
      socket.add(
        '${jsonEncode({
          'type': 1,
          'target': target,
          'arguments': [payload],
        })}$_rs',
      );
    }
  }

  /// Drops every connection, like a backend restart or a network blip.
  Future<void> dropConnections() async {
    for (final socket in [..._sockets]) {
      await socket.close();
    }
  }

  Future<void> close() => _server.close(force: true);
}

void main() {
  test(
    'reconnected fires when the connection comes back, and joining again gets session-group broadcasts (#365)',
    () async {
      final hub = _FakeHub();
      await hub.start();
      addTearDown(hub.close);
      final client = SessionHubClient(
        apiBaseUrl: hub.baseUrl,
        tokenProvider: () async => 'token',
      );
      addTearDown(client.dispose);

      await client.connect();
      await client.joinSession(_sessionId);
      expect(hub.joinCalls, 1);

      final reconnected = client.reconnected.first;
      await hub.dropConnections();
      await reconnected.timeout(const Duration(seconds: 10));

      // The reconnected connection is in no session group until it joins.
      await client.joinSession(_sessionId);
      expect(hub.joinCalls, 2);

      final next = client.events.first;
      hub.broadcastToSessionGroup('ParticipantStateChanged', {
        'sessionId': _sessionId,
        'userId': 'Ada',
      });
      final event = await next.timeout(const Duration(seconds: 10));
      expect(event.kind, 'ParticipantStateChanged');
      expect(event.payload['userId'], 'Ada');
    },
  );
}
