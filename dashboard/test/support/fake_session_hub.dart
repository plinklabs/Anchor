import 'dart:convert';
import 'dart:io';

const _rs = '\u001e';

/// One hub method a client invoked: its name and its arguments as they came
/// over the wire.
typedef HubInvocation = ({String target, List<Object?> arguments});

/// A minimal SignalR hub over WebSockets and the JSON protocol, for tests that
/// drive the real [SessionHubClient]: it answers negotiate and the handshake,
/// records every invocation and completes it, adds a connection to the session
/// group on JoinSession, and broadcasts to that group or to every connection.
/// Like the real hub, a new connection (a reconnect included) is in no session
/// group.
class FakeSessionHub {
  /// A hub served at [path], as the backend maps it.
  FakeSessionHub({this.path = '/hubs/session'});

  final String path;
  late final HttpServer _server;
  final _sockets = <WebSocket>{};
  final _sessionGroup = <WebSocket>{};
  int _nextConnection = 0;
  int joinCalls = 0;

  /// Every invocation from every connection, in the order they came.
  final invocations = <HubInvocation>[];

  Uri get baseUrl => Uri.parse('http://127.0.0.1:${_server.port}/');

  Future<void> start() async {
    _server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
    _server.listen(_handle);
  }

  Future<void> _handle(HttpRequest request) async {
    if (request.uri.path == '$path/negotiate') {
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
    if (request.uri.path == path &&
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
        continue;
      }
      if (message['type'] != 1) continue;
      final target = message['target'] as String;
      final arguments = (message['arguments'] as List?) ?? const [];
      invocations.add((target: target, arguments: arguments));
      Object? result;
      if (target == 'JoinSession') {
        joinCalls++;
        _sessionGroup.add(socket);
        final request = arguments.firstOrNull;
        result = {
          'sessionId': request is Map ? request['sessionId'] : null,
          'userId': 'teacher',
        };
      }
      final invocationId = message['invocationId'];
      if (invocationId != null) {
        socket.add(
          '${jsonEncode({'type': 3, 'invocationId': invocationId, 'result': ?result})}$_rs',
        );
      }
    }
  }

  void broadcastToSessionGroup(String target, Map<String, dynamic> payload) =>
      _send(_sessionGroup, target, [payload]);

  /// Sends [target] with [arguments] to every connection, as a broadcast to
  /// a user group the connection is in reaches it.
  void sendToAll(String target, List<Object?> arguments) =>
      _send(_sockets, target, arguments);

  void _send(Iterable<WebSocket> to, String target, List<Object?> arguments) {
    for (final socket in to) {
      socket.add(
        '${jsonEncode({'type': 1, 'target': target, 'arguments': arguments})}$_rs',
      );
    }
  }

  /// Drops every connection, like a backend restart or a network blip.
  Future<void> dropConnections() async {
    for (final socket in [..._sockets]) {
      await socket.close();
    }
  }

  /// Closes every connection and forbids reconnecting, as the server does
  /// when it turns a connection away. SignalR then closes rather than retries.
  void refuseConnections() {
    for (final socket in _sockets) {
      socket.add(
        '${jsonEncode({'type': 7, 'error': 'Refused.', 'allowReconnect': false})}$_rs',
      );
    }
  }

  Future<void> close() => _server.close(force: true);
}
