import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/feed/feed_history.dart';
import 'package:anchor_dashboard/feed/live_feed.dart';
import 'package:anchor_dashboard/l10n/app_localizations.dart';
import 'package:anchor_dashboard/realtime/session_hub_client.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';

// #492: the live page's feed held only what the page heard, so it was empty
// each time the teacher came back to a running session. It now loads what
// happened before from the events the backend stored, rebuilt in the hub's
// shapes so the feed reads them as it reads the live ones (#491). These pin
// that rebuild and how it merges with the live events; the page is in
// session_page_feed_test.dart, the real app in
// integration_test/live_session_test.dart.

const _sessionId = '5fbae478-0000-0000-0000-000000000492';
const _adaId = 'edee9562-0000-0000-0000-00000000000a';
const _boId = 'edee9562-0000-0000-0000-00000000000b';

final AppLocalizations en = lookupAppLocalizations(const Locale('en'));
final AppLocalizations nl = lookupAppLocalizations(const Locale('nl'));

final DateTime _start = DateTime.utc(2026, 11, 11, 8, 25);
DateTime _min(int minutes, [int ms = 0]) =>
    _start.add(Duration(minutes: minutes, milliseconds: ms));

SessionRecentEvent _stored(
  String kind,
  DateTime at, {
  String userId = _adaId,
  String payloadJson = '{}',
}) => SessionRecentEvent(
  id: '$kind-$userId-${at.microsecondsSinceEpoch}',
  userId: userId,
  kind: kind,
  payloadJson: payloadJson,
  occurredAt: at,
);

SessionParticipantInfo _participant(
  String id,
  String name, {
  DateTime? joinedAt,
  DateTime? leftAt,
}) => SessionParticipantInfo(
  userId: id,
  displayName: name,
  joinedAt: joinedAt,
  declinedAt: null,
  leftAt: leftAt,
  state: leftAt == null
      ? ParticipantLiveState.joined
      : ParticipantLiveState.left,
);

/// The roster, as the page looks students up in it.
String? _roster(String userId) =>
    const {_adaId: 'Ada', _boId: 'Bo', 'c': 'Cy'}[userId];

/// The sentences the feed shows for [events], all activity unless [showAll]
/// is off, as the page shows them.
List<String> _sentences(List<SessionEvent> events, {bool showAll = true}) => [
  for (final FeedEntry e in feedEntries(
    en,
    events,
    showAll: showAll,
    nameOf: _roster,
  ))
    e.count > 1 ? '${e.sentence} ×${e.count}' : e.sentence,
];

String? _sentence(SessionRecentEvent stored, {String? name = 'Ada'}) {
  final SessionEvent? event = feedEventOfStored(
    stored,
    sessionId: _sessionId,
    name: name,
  );
  return event == null ? null : feedSentence(en, event, nameOf: _roster);
}

void main() {
  group('a stored event reads as the live one', () {
    test('every kind the feed has a row for', () {
      expect(
        _sentence(
          _stored(
            'UnblockRequest',
            _min(1),
            payloadJson:
                '{"url":"https://chat.example.com/x","host":" Chat.Example.COM "}',
          ),
        ),
        'Ada asks for access to chat.example.com.',
      );
      expect(
        _sentence(
          _stored(
            'TamperDetected',
            _min(1),
            payloadJson: '{"kind":"inprivate_opened"}',
          ),
        ),
        'Ada opened an InPrivate window.',
      );
      expect(
        _sentence(
          _stored(
            'TamperDetected',
            _min(1),
            payloadJson: '{"kind":"extension_silent"}',
          ),
        ),
        "Ada's Anchor extension is off or not responding.",
      );
      expect(
        _sentence(_stored('TamperDetected', _min(1), payloadJson: '{}')),
        "Tampering detected on Ada's computer.",
      );
      expect(
        _sentence(
          _stored(
            'HeartbeatLost',
            _min(1),
            payloadJson: '{"lastSeenAt":"2026-11-11T08:25:30.1234567+00:00"}',
          ),
        ),
        "Ada's agent stopped reporting.",
      );
      expect(
        _sentence(_stored('AgentReconnected', _min(1))),
        'Ada is connected again.',
      );
      expect(
        _sentence(
          _stored(
            'AgentMissing',
            _min(1),
            payloadJson: '{"expectedSince":"2026-11-11T08:25:00+00:00"}',
          ),
        ),
        "Ada's agent hasn't reported in.",
      );
      expect(
        _sentence(
          _stored(
            'JoinDeclined',
            _min(1),
            payloadJson: '{"reason":"user_cancelled"}',
          ),
        ),
        'Ada declined the session.',
      );
      expect(
        _sentence(_stored('ManualLeave', _min(1))),
        'Ada left the session.',
      );
      expect(
        _sentence(_stored('AgentKilled', _min(1))),
        'Ada left the session.',
      );
    });

    test('in the hub shape the feed reads, with its priority', () {
      final SessionEvent asks = feedEventOfStored(
        _stored(
          'UnblockRequest',
          _min(1),
          payloadJson:
              '{"url":"https://chat.example.com/","host":"chat.example.com"}',
        ),
        sessionId: _sessionId,
        name: 'Ada',
      )!;
      expect(asks.kind, 'UnblockRequested');
      expect(asks.at, _min(1));
      expect(asks.payload, {
        'sessionId': _sessionId,
        'userId': _adaId,
        'userDisplayName': 'Ada',
        'host': 'chat.example.com',
        'url': 'https://chat.example.com/',
        'requestedAt': '2026-11-11T08:26:00.000Z',
      });
      expect(feedPriorityOf(asks), FeedPriority.attention);

      final SessionEvent missing = feedEventOfStored(
        _stored('AgentMissing', _min(1)),
        sessionId: _sessionId,
      )!;
      expect(missing.payload['state'], 'AgentMissing');
      expect(feedPriorityOf(missing), FeedPriority.warning);
    });

    test('in Dutch too', () {
      final SessionEvent left = feedEventOfStored(
        _stored('ManualLeave', _min(1)),
        sessionId: _sessionId,
        name: 'Ada',
      )!;
      expect(feedSentence(nl, left), 'Ada heeft de sessie verlaten.');
    });

    test('a student the event does not name, the roster names', () {
      final SessionEvent lost = feedEventOfStored(
        _stored('HeartbeatLost', _min(1)),
        sessionId: _sessionId,
      )!;
      expect(
        feedSentence(en, lost, nameOf: (id) => id == _adaId ? 'Ada' : null),
        "Ada's agent stopped reporting.",
      );
      expect(feedSentence(en, lost), "A student's agent stopped reporting.");
    });

    test('nothing for what the hub never tells the teacher, and for an access '
        'request without a site, whose broadcast the backend skips', () {
      for (final String kind in const [
        'ForegroundChange',
        'BlockedUrl',
        'UnblockApproved',
        'SomethingNew',
      ]) {
        expect(
          feedEventOfStored(
            _stored(kind, _min(1), payloadJson: '{"host":"example.com"}'),
            sessionId: _sessionId,
          ),
          isNull,
          reason: kind,
        );
      }
      for (final String payload in const [
        '{"url":"https://chat.example.com/"}',
        '{"host":"  "}',
        'not json',
      ]) {
        expect(
          feedEventOfStored(
            _stored('UnblockRequest', _min(1), payloadJson: payload),
            sessionId: _sessionId,
          ),
          isNull,
          reason: payload,
        );
      }
    });

    test('the feed asks for exactly the kinds it has a row for', () {
      for (final String kind in feedStoredKinds) {
        expect(
          feedEventOfStored(
            _stored(kind, _min(1), payloadJson: '{"host":"example.com"}'),
            sessionId: _sessionId,
          ),
          isNotNull,
          reason: kind,
        );
      }
    });
  });

  group('the session so far', () {
    test('its stored events and each student joining, newest first, without '
        "the session's start, which the teacher's page doesn't hear", () {
      final List<SessionEvent> history = feedHistory(
        sessionId: _sessionId,
        stored: [
          _stored(
            'TamperDetected',
            _min(9),
            payloadJson: '{"kind":"inprivate_opened"}',
          ),
          _stored(
            'UnblockRequest',
            _min(4),
            userId: _boId,
            payloadJson: '{"host":"chat.example.com"}',
          ),
        ],
        participants: [
          _participant(_adaId, 'Ada', joinedAt: _min(1)),
          _participant(_boId, 'Bo', joinedAt: _min(2)),
          _participant('c', 'Cy'),
        ],
      );

      expect(_sentences(history), [
        'Ada opened an InPrivate window.',
        'Bo asks for access to chat.example.com.',
        'Bo joined.',
        'Ada joined.',
      ]);
      expect(history.map((e) => e.at), [_min(9), _min(4), _min(2), _min(1)]);
      // By default, only what needs the teacher.
      expect(_sentences(history, showAll: false), [
        'Ada opened an InPrivate window.',
        'Bo asks for access to chat.example.com.',
      ]);
    });

    test("a student who is out left at the server's time, which the live "
        'page heard, even when their leave was never reported', () {
      final List<SessionEvent> history = feedHistory(
        sessionId: _sessionId,
        stored: [
          // The agent's clock, a moment before the server recorded it.
          _stored('ManualLeave', _min(7, 400)),
          _stored('AgentKilled', _min(3), userId: _boId),
          _stored('ManualLeave', _min(2)),
        ],
        participants: [
          _participant(_adaId, 'Ada', joinedAt: _min(1), leftAt: _min(7, 900)),
          // Back in the session since.
          _participant(_boId, 'Bo', joinedAt: _min(1)),
          // Out, but their leave never reached the backend as an event.
          _participant('c', 'Cy', joinedAt: _min(1), leftAt: _min(5)),
        ],
      );

      final List<SessionEvent> left = [
        for (final SessionEvent e in history)
          if (e.payload['state'] == 'Left') e,
      ];
      expect(
        left.map((e) => (e.payload['displayName'], e.at, e.payload['at'])),
        [
          ('Ada', _min(7, 900), '2026-11-11T08:32:00.900Z'),
          ('Cy', _min(5), '2026-11-11T08:30:00.000Z'),
          ('Bo', _min(3), '2026-11-11T08:28:00.000Z'),
          ('Ada', _min(2), '2026-11-11T08:27:00.000Z'),
        ],
      );
    });

    test('cut at the limit, it starts at its oldest stored event, not next to '
        'a gap', () {
      final List<SessionEvent> history = feedHistory(
        sessionId: _sessionId,
        stored: [
          _stored('HeartbeatLost', _min(9)),
          _stored('AgentReconnected', _min(6)),
        ],
        participants: [
          _participant(_adaId, 'Ada', joinedAt: _min(1)),
          _participant(_boId, 'Bo', joinedAt: _min(6)),
          _participant('c', 'Cy', joinedAt: _min(8), leftAt: _min(8, 5)),
        ],
        complete: false,
      );

      // At the same moment, the stored event first.
      expect(_sentences(history), [
        "Ada's agent stopped reporting.",
        'Cy left the session.',
        'Cy joined.',
        'Ada is connected again.',
        'Bo joined.',
      ]);
    });

    test('without the detail, its stored events alone', () {
      final List<SessionEvent> history = feedHistory(
        sessionId: _sessionId,
        stored: [_stored('ManualLeave', _min(3), userId: 'someone')],
      );
      expect(_sentences(history), ['A student left the session.']);
    });
  });

  group('with what the page hears live', () {
    // As the hub broadcasts them: the server's times in .NET's notation.
    SessionEvent live(String kind, Map<String, dynamic> payload) =>
        SessionEvent(
          kind: kind,
          payload: {'sessionId': _sessionId, 'userId': _adaId, ...payload},
          at: DateTime.now(),
        );

    test('the live events come above the history from before them, and an '
        'event the page heard live and loaded too shows once', () {
      final List<SessionEvent> history = feedHistory(
        sessionId: _sessionId,
        stored: [
          _stored(
            'UnblockRequest',
            DateTime.parse('2026-11-11T08:30:00.1234567+00:00'),
            payloadJson: '{"host":"Chat.example.com"}',
          ),
          _stored(
            'UnblockRequest',
            _min(2),
            payloadJson: '{"host":"chat.example.com"}',
          ),
          _stored('ManualLeave', _min(1), userId: _boId),
        ],
        participants: [
          _participant(_adaId, 'Ada'),
          _participant(_boId, 'Bo', leftAt: _min(1, 250)),
        ],
      );
      final List<SessionEvent> heard = [
        live('HeartbeatLost', {'lastSeenAt': '2026-11-11T08:31:00+00:00'}),
        // Between the join and the load: live, and stored as well.
        live('UnblockRequested', {
          'userDisplayName': 'Ada',
          'host': 'chat.example.com',
          'url': 'https://chat.example.com/',
          'requestedAt': '2026-11-11T08:30:00.1234567+00:00',
        }),
      ];

      expect(_sentences(feedTimeline(heard, history)), [
        "Ada's agent stopped reporting.",
        'Ada asks for access to chat.example.com. ×2',
        'Bo left the session.',
      ]);
      expect(feedTimeline(heard, history), hasLength(4));

      // The leave, heard live at the server's time: once too. The page heard
      // it before it missed the requests (#499), so it goes below them.
      final List<SessionEvent> leftLive = [
        live('ParticipantStateChanged', {
          'userId': _boId,
          'displayName': 'Bo',
          'state': 'Left',
          'at': '2026-11-11T08:26:00.2500000+00:00',
        }),
      ];
      expect(_sentences(feedTimeline(leftLive, history)), [
        'Ada asks for access to chat.example.com. ×2',
        'Bo left the session.',
      ]);
      expect(feedTimeline(leftLive, history), hasLength(3));
    });

    test('what the page missed while it was offline goes in its place in '
        'time, between what it heard before and after, each once (#499)', () {
      // The page opened at 08:28, heard the request and Bo leaving, went
      // offline at 08:33 and missed Ada's tamper and Cy declining, came
      // back at 08:37, heard Ada's agent come back while it loaded the
      // history again, and then that it stopped again.
      final List<SessionEvent> history = feedHistory(
        sessionId: _sessionId,
        stored: [
          _stored('AgentReconnected', _min(13)),
          _stored(
            'TamperDetected',
            _min(11),
            payloadJson: '{"kind":"inprivate_opened"}',
          ),
          _stored('JoinDeclined', _min(9), userId: 'c'),
          _stored(
            'UnblockRequest',
            _min(5),
            payloadJson: '{"host":"chat.example.com"}',
          ),
          _stored(
            'UnblockRequest',
            _min(2),
            payloadJson: '{"host":"maps.example.com"}',
          ),
        ],
        participants: [
          _participant(_adaId, 'Ada'),
          _participant(_boId, 'Bo', leftAt: _min(6, 500)),
          _participant('c', 'Cy'),
        ],
      );
      final List<SessionEvent> heard = [
        live('HeartbeatLost', {'lastSeenAt': '2026-11-11T08:39:00+00:00'}),
        live('AgentReconnected', {'reconnectedAt': '2026-11-11T08:38:00Z'}),
        live('ParticipantStateChanged', {
          'userId': _boId,
          'displayName': 'Bo',
          'state': 'Left',
          'at': '2026-11-11T08:31:00.5000000+00:00',
        }),
        live('UnblockRequested', {
          'userDisplayName': 'Ada',
          'host': 'chat.example.com',
          'requestedAt': '2026-11-11T08:30:00Z',
        }),
      ];

      expect(_sentences(feedTimeline(heard, history)), [
        "Ada's agent stopped reporting.",
        'Ada is connected again.',
        'Ada opened an InPrivate window.',
        'Cy declined the session.',
        'Bo left the session.',
        'Ada asks for access to chat.example.com.',
        'Ada asks for access to maps.example.com.',
      ]);
      expect(feedTimeline(heard, history), hasLength(7));
    });

    test('an agent that stopped reporting takes the time the server noticed, '
        'not when it was last seen', () {
      final List<SessionEvent> history = feedHistory(
        sessionId: _sessionId,
        stored: [
          _stored(
            'HeartbeatLost',
            _min(6),
            payloadJson: '{"lastSeenAt":"2026-11-11T08:30:00+00:00"}',
          ),
          _stored(
            'TamperDetected',
            _min(5, 500),
            payloadJson: '{"kind":"inprivate_opened"}',
          ),
        ],
      );
      final List<SessionEvent> heard = [
        live('HeartbeatLost', {'lastSeenAt': '2026-11-11T08:30:00Z'}),
      ];
      expect(_sentences(feedTimeline(heard, history)), [
        "Ada's agent stopped reporting.",
        'Ada opened an InPrivate window.',
      ]);
    });

    test('the live events keep the order they came in, whatever time they '
        'carry', () {
      // Bo's laptop runs ten minutes behind: his request, heard after Ada's
      // leave, says 08:20.
      final List<SessionEvent> history = feedHistory(
        sessionId: _sessionId,
        stored: [
          _stored(
            'TamperDetected',
            _min(7),
            payloadJson: '{"kind":"inprivate_opened"}',
          ),
          _stored('JoinDeclined', _min(0), userId: 'c'),
        ],
      );
      final List<SessionEvent> heard = [
        live('AgentReconnected', {
          'userId': _boId,
          'reconnectedAt': '2026-11-11T08:35:00Z',
        }),
        live('UnblockRequested', {
          'userId': _boId,
          'userDisplayName': 'Bo',
          'host': 'chat.example.com',
          'requestedAt': '2026-11-11T08:20:00Z',
        }),
        live('ParticipantStateChanged', {
          'displayName': 'Ada',
          'state': 'Left',
          'at': '2026-11-11T08:30:00Z',
        }),
      ];
      expect(_sentences(feedTimeline(heard, history)), [
        'Bo is connected again.',
        'Ada opened an InPrivate window.',
        'Bo asks for access to chat.example.com.',
        'Ada left the session.',
        'Cy declined the session.',
      ]);
    });

    test('a live event that carries no time stays above the history next to '
        'it', () {
      final List<SessionEvent> history = feedHistory(
        sessionId: _sessionId,
        stored: [
          _stored(
            'TamperDetected',
            _min(7),
            payloadJson: '{"kind":"inprivate_opened"}',
          ),
        ],
      );
      final List<SessionEvent> heard = [
        live('SessionEnded', const {}),
        live('ParticipantStateChanged', {
          'userId': _boId,
          'displayName': 'Bo',
          'state': 'Left',
          'at': '2026-11-11T08:30:00Z',
        }),
      ];
      expect(_sentences(feedTimeline(heard, history)), [
        'Session ended.',
        'Ada opened an InPrivate window.',
        'Bo left the session.',
      ]);
    });

    test('the same thing said at another time is another event', () {
      final List<SessionEvent> history = feedHistory(
        sessionId: _sessionId,
        stored: [_stored('AgentReconnected', _min(2))],
      );
      final List<SessionEvent> heard = [
        live('AgentReconnected', {'reconnectedAt': '2026-11-11T08:40:00Z'}),
      ];
      expect(feedTimeline(heard, history), hasLength(2));
    });

    test('with nothing loaded, what the page heard', () {
      final List<SessionEvent> heard = [live('SessionEnded', const {})];
      expect(feedTimeline(heard, const []), same(heard));
      expect(feedEventIdentity(heard.single), isNull);
    });
  });

  group('loaded again after a reconnect (#499)', () {
    SessionEvent asks(String host, DateTime at) => feedEventOfStored(
      _stored('UnblockRequest', at, payloadJson: '{"host":"$host"}'),
      sessionId: _sessionId,
      name: 'Ada',
    )!;

    test('what the backend has now replaces what it had', () {
      final List<SessionEvent> previous = [asks('a.example.com', _min(2))];
      final List<SessionEvent> fresh = [
        asks('b.example.com', _min(9)),
        asks('a.example.com', _min(2)),
      ];
      expect(feedHistoryReloaded(previous, fresh), same(fresh));
    });

    test('cut at its limit, it keeps what it had from before the cut', () {
      final List<SessionEvent> previous = [
        asks('c.example.com', _min(6)),
        asks('b.example.com', _min(4)),
        asks('a.example.com', _min(2)),
      ];
      final List<SessionEvent> fresh = [
        asks('d.example.com', _min(9)),
        asks('c.example.com', _min(6)),
        asks('b.example.com', _min(4)),
      ];
      expect(_sentences(feedHistoryReloaded(previous, fresh, cutAt: _min(4))), [
        'Ada asks for access to d.example.com.',
        'Ada asks for access to c.example.com.',
        'Ada asks for access to b.example.com.',
        'Ada asks for access to a.example.com.',
      ]);
    });
  });
}
