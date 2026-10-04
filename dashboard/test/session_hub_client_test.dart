import 'package:anchor_dashboard/realtime/session_hub_client.dart';
import 'package:fake_async/fake_async.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/io_client.dart';
import 'package:signalr_core/signalr_core.dart' show RetryContext;

import 'support/fake_session_hub.dart';

// #365: the real SessionHubClient against an in-process SignalR hub. SignalR
// keeps no group membership across a reconnect, so the client has to tell its
// page when the connection comes back; the page then joins its session again.
//
// #370: SignalR's default retry policy gives up about 42 s after a drop and
// closes the connection for good. The client has to keep retrying for as long
// as the page is open, say when the feed isn't getting through, and start a
// closed connection again on request.

const _sessionId = 'aaaaaaaa-0000-0000-0000-000000000001';

/// The network between the client and the hub. While it's [down] a request
/// fails without reaching the hub, as when the backend is unreachable, and
/// [failedAt] records when, on the client's clock.
class _Network extends http.BaseClient {
  _Network(this._clock);

  final Duration Function() _clock;
  final _inner = IOClient();
  bool down = false;
  final failedAt = <Duration>[];

  @override
  Future<http.StreamedResponse> send(http.BaseRequest request) async {
    if (down) {
      failedAt.add(_clock());
      throw http.ClientException('Connection refused', request.url);
    }
    return _inner.send(request);
  }

  @override
  void close() => _inner.close();
}

/// Lets the real I/O between a client running in [fake] time and the hub run,
/// without moving the fake clock, until [done].
Future<void> _settle(FakeAsync fake, bool Function() done, String what) async {
  final deadline = DateTime.now().add(const Duration(seconds: 10));
  while (true) {
    fake.elapse(Duration.zero);
    if (done()) return;
    if (DateTime.now().isAfter(deadline)) fail('Timed out waiting for $what');
    await Future<void>.delayed(const Duration(milliseconds: 5));
  }
}

/// Runs [body] in [fake] time and waits for it, letting its I/O run.
Future<T> _inFakeTime<T>(FakeAsync fake, Future<T> Function() body) async {
  late final Future<T> future;
  var done = false;
  fake.run((_) {
    future = body();
    future.then((_) => done = true, onError: (Object _) => done = true);
  });
  await _settle(fake, () => done, 'the call to finish');
  return future;
}

void main() {
  test('the retry policy never gives up, and waits at most 30 s (#370)', () {
    const policy = SessionHubRetryPolicy();
    int delay(int attempt) => policy.nextRetryDelayInMilliseconds(
      RetryContext(previousRetryCount: attempt),
    );

    expect(
      [for (var i = 0; i < 7; i++) delay(i)],
      [1000, 2000, 4000, 8000, 16000, 30000, 30000],
    );
    for (final attempt in [10, 11, 100, 100000]) {
      expect(delay(attempt), SessionHubRetryPolicy.maxDelay.inMilliseconds);
    }
  });

  test(
    'a dropped connection keeps retrying long past the default 42 s, and a late reconnect still rejoins the session (#370)',
    () async {
      final hub = FakeSessionHub();
      await hub.start();
      addTearDown(hub.close);
      final fake = FakeAsync();
      final network = _Network(() => fake.elapsed);
      addTearDown(network.close);
      final states = <SessionHubLinkState>[];
      final events = <SessionEvent>[];
      var reconnects = 0;
      late final SessionHubClient client;
      fake.run((_) {
        client = SessionHubClient(
          apiBaseUrl: hub.baseUrl,
          tokenProvider: () async => 'token',
          httpClient: network,
        );
        client.linkState.listen(states.add);
        client.reconnected.listen((_) => reconnects++);
        client.events.listen(events.add);
      });
      addTearDown(() => _inFakeTime(fake, client.dispose));

      await _inFakeTime(fake, client.connect);
      await _inFakeTime(fake, () => client.joinSession(_sessionId));
      expect(states, [SessionHubLinkState.connected]);
      expect(hub.joinCalls, 1);

      // The backend goes away for ten minutes.
      network.down = true;
      await hub.dropConnections();
      await _settle(
        fake,
        () => states.contains(SessionHubLinkState.reconnecting),
        'the drop',
      );
      final droppedAt = fake.elapsed;
      fake.elapse(const Duration(minutes: 10));

      // It never gave up: it's still reconnecting, and it kept trying long
      // after the 42 s at which SignalR's default policy closes for good.
      expect(states, [
        SessionHubLinkState.connected,
        SessionHubLinkState.reconnecting,
      ]);
      expect(
        network.failedAt.where(
          (at) => at - droppedAt > const Duration(seconds: 42),
        ),
        hasLength(greaterThan(15)),
      );
      expect(reconnects, 0);
      // It backed off to every 30 s, like the agent.
      final gaps = [
        for (var i = 0; i < network.failedAt.length; i++)
          network.failedAt[i] - (i == 0 ? droppedAt : network.failedAt[i - 1]),
      ];
      expect(gaps.take(6), const [
        Duration(seconds: 1),
        Duration(seconds: 2),
        Duration(seconds: 4),
        Duration(seconds: 8),
        Duration(seconds: 16),
        Duration(seconds: 30),
      ]);
      expect(gaps.skip(5), everyElement(const Duration(seconds: 30)));

      // The backend is back: the next retry, at most 30 s on, gets through.
      network.down = false;
      final failures = network.failedAt.length;
      fake.elapse(SessionHubRetryPolicy.maxDelay);
      await _settle(fake, () => reconnects == 1, 'the late reconnect');
      expect(network.failedAt, hasLength(failures));
      expect(states.last, SessionHubLinkState.connected);

      // And the page's rejoin (#365) still works on it.
      await _inFakeTime(fake, () => client.joinSession(_sessionId));
      expect(hub.joinCalls, 2);
      hub.broadcastToSessionGroup('ParticipantStateChanged', {
        'sessionId': _sessionId,
        'userId': 'Ada',
      });
      await _settle(fake, () => events.isNotEmpty, 'the broadcast');
      expect(events.single.kind, 'ParticipantStateChanged');
      expect(events.single.payload['userId'], 'Ada');
    },
  );

  test(
    'a connection the server closed reports it, and restart brings it back and fires reconnected (#370)',
    () async {
      final hub = FakeSessionHub();
      await hub.start();
      addTearDown(hub.close);
      final client = SessionHubClient(
        apiBaseUrl: hub.baseUrl,
        tokenProvider: () async => 'token',
      );
      addTearDown(client.dispose);
      final states = <SessionHubLinkState>[];
      client.linkState.listen(states.add);

      await client.connect();
      await client.joinSession(_sessionId);

      final closed = client.linkState.firstWhere(
        (s) => s == SessionHubLinkState.disconnected,
      );
      hub.refuseConnections();
      await closed.timeout(const Duration(seconds: 10));
      // The server forbade a reconnect, so nothing retried.
      expect(states, [
        SessionHubLinkState.connected,
        SessionHubLinkState.disconnected,
      ]);

      final reconnected = client.reconnected.first;
      await client.restart();
      await reconnected.timeout(const Duration(seconds: 10));
      expect(states.skip(2), [
        SessionHubLinkState.reconnecting,
        SessionHubLinkState.connected,
      ]);

      await client.joinSession(_sessionId);
      expect(hub.joinCalls, 2);
      final next = client.events.first;
      hub.broadcastToSessionGroup('ParticipantStateChanged', {
        'sessionId': _sessionId,
        'userId': 'Ada',
      });
      final event = await next.timeout(const Duration(seconds: 10));
      expect(event.kind, 'ParticipantStateChanged');
    },
  );

  test(
    'a first connect that fails reports it, and restart connects it (#370)',
    () async {
      final hub = FakeSessionHub();
      await hub.start();
      addTearDown(hub.close);
      final network = _Network(() => Duration.zero)..down = true;
      addTearDown(network.close);
      final client = SessionHubClient(
        apiBaseUrl: hub.baseUrl,
        tokenProvider: () async => 'token',
        httpClient: network,
      );
      addTearDown(client.dispose);
      final states = <SessionHubLinkState>[];
      client.linkState.listen(states.add);

      await expectLater(client.connect(), throwsA(anything));
      await pumpEventQueue();
      expect(states, [SessionHubLinkState.disconnected]);

      // Still down: it stays disconnected.
      await client.restart();
      await pumpEventQueue();
      expect(states.last, SessionHubLinkState.disconnected);

      network.down = false;
      final reconnected = client.reconnected.first;
      await client.restart();
      await reconnected.timeout(const Duration(seconds: 10));
      expect(states.last, SessionHubLinkState.connected);
      await client.joinSession(_sessionId);
      expect(hub.joinCalls, 1);
    },
  );

  test(
    'reconnected fires when the connection comes back, and joining again gets session-group broadcasts (#365)',
    () async {
      final hub = FakeSessionHub();
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
