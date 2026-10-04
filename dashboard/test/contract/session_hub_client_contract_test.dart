import 'package:anchor_dashboard/realtime/session_hub_client.dart';
import 'package:flutter_test/flutter_test.dart';

import '../support/fake_session_hub.dart';
import '../support/session_hub_contract.dart';

// #390: the hub client's side of contracts/session-hub.json. The real
// SessionHubClient against an in-process hub served where the contract says
// the backend maps it. The hub sends every event the contract lists, in the
// shape the contract gives it, and the client has to pass each one on with its
// payload; the hub methods the client calls have to go out in the shape the
// contract gives them. Backend CI checks the backend against the same file.

const _sessionId = 'aaaaaaaa-0000-0000-0000-000000000001';

void main() {
  final contract = SessionHubContract.load();
  late FakeSessionHub hub;
  late SessionHubClient client;

  setUp(() async {
    hub = FakeSessionHub(path: contract.hubPath);
    await hub.start();
    client = SessionHubClient(
      apiBaseUrl: hub.baseUrl,
      tokenProvider: () async => 'token',
    );
    await client.connect();
  });

  tearDown(() async {
    await client.dispose();
    await hub.close();
  });

  test(
    'the client passes on every event in the contract, with its payload',
    () async {
      final received = <String, Map<String, dynamic>>{};
      final sub = client.events.listen((e) => received[e.kind] = e.payload);
      addTearDown(sub.cancel);

      for (final event in contract.events) {
        hub.sendToAll(event.name, [event.wireArgument(_sessionId)]);
      }
      // An event without a handler never arrives: give the rest time to.
      final deadline = DateTime.now().add(const Duration(seconds: 5));
      while (received.length < contract.events.length &&
          DateTime.now().isBefore(deadline)) {
        await Future<void>.delayed(const Duration(milliseconds: 10));
      }

      expect(received, {
        for (final event in contract.events)
          event.name: event.payload(_sessionId),
      });
    },
  );

  test(
    'the client calls the hub methods in the contract, in the shape it gives',
    () async {
      await client.joinSession(_sessionId);
      await client.leaveSession(_sessionId);

      expect(
        hub.invocations.map((i) => i.target),
        unorderedEquals(contract.methods.map((m) => m.name)),
      );
      for (final invocation in hub.invocations) {
        final method = contract.methods.singleWhere(
          (m) => m.name == invocation.target,
        );
        final argument = invocation.arguments.single;
        if (method.argument != null) {
          // A bare value: the session id, as its name says.
          expect(
            argument,
            method.wireArgument(_sessionId),
            reason: '${method.name} sends ${method.argument}',
          );
        } else {
          expect(argument, isA<Map<String, dynamic>>());
          expect(
            (argument as Map<String, dynamic>).keys,
            unorderedEquals(method.fields),
            reason: '${method.name} sends an object with ${method.fields}',
          );
          expect(argument['sessionId'], _sessionId);
        }
      }
    },
  );
}
