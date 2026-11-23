import 'dart:io';

import 'package:anchor_dashboard/demo/demo_data.dart';
import 'package:flutter_test/flutter_test.dart';

/// Issue #586: a join code is six digits. The backend hands out nothing else
/// (`SessionsController.GenerateUniqueJoinCodeAsync`) and turns anything else
/// away (`JoinByCode`: "Join code must be 6 digits."), and the agent's "Join
/// by code" takes six digits and nothing else. No client shares that rule, so
/// it is written out here. The demo once gave the live session shot the code
/// PLINK-3B, which no student could type in.
final RegExp _joinCodeShape = RegExp(r'^\d{6}$');

/// Issue #514: the website's dashboard screenshots are taken from the demo
/// data (screenshots/generate-screenshots.mjs), and the website describes the
/// intended product: the teacher never sees which apps a student used or
/// which pages were blocked (website teachers.html#history). The demo once
/// gave the past session only app switches and blocked sites, so its shot
/// showed the pilot's test-phase reporting as the product.
///
/// The kinds only the pilot builds report (#515, #526): an app switch, a
/// blocked site, a site the extension let through.
const Set<String> _testPhaseKinds = {
  'ForegroundChange',
  'BlockedUrl',
  'UnfilteredSite',
};

void main() {
  test('every join code the demo hands out has the shape of a real one', () {
    expect(demoJoinCode, matches(_joinCodeShape));
    expect(demoLiveSessionDetail().joinCode, matches(_joinCodeShape));
    expect(demoPastSessionDetail().joinCode, matches(_joinCodeShape));
    final started = demoLiveEvents().firstWhere(
      (e) => e.kind == 'SessionStarted',
    );
    expect(started.payload['joinCode'], matches(_joinCodeShape));
  });

  test('the agent and extension screenshot demos show the same code', () {
    // The website's agent and extension shots come from their own demo
    // content; the three tell one story (#251), so they share one code.
    final agent = File(
      '../agent/src/FocusAgent.App/SelfTestDemoContent.cs',
    ).readAsStringSync();
    final extension = File(
      '../extension/store-listing/generate-screenshots.mjs',
    ).readAsStringSync();
    expect(
      RegExp(r'JoinCode = "([^"]*)"').firstMatch(agent)?.group(1),
      demoJoinCode,
      reason: 'SelfTestDemoContent.JoinCode',
    );
    expect(
      RegExp(r"joinCode: '([^']*)'").firstMatch(extension)?.group(1),
      demoJoinCode,
      reason: 'DEMO_SESSION.joinCode in the store-listing generator',
    );
  });

  test('the past session holds none of the pilot test-phase reports', () {
    final detail = demoPastSessionDetail();
    expect(
      detail.summaries.map((s) => s.kind).toSet().intersection(_testPhaseKinds),
      isEmpty,
    );
    expect(
      detail.recentEvents
          .map((e) => e.kind)
          .toSet()
          .intersection(_testPhaseKinds),
      isEmpty,
    );
  });

  test('the live session holds none of the pilot test-phase reports', () {
    expect(
      demoLiveEvents().map((e) => e.kind).toSet().intersection(_testPhaseKinds),
      isEmpty,
    );
    final live = demoLiveSessionDetail();
    expect(
      <String>{
        ...live.summaries.map((s) => s.kind),
        ...live.recentEvents.map((e) => e.kind),
      }.intersection(_testPhaseKinds),
      isEmpty,
    );
  });

  test('the past session shows what the website says a past session keeps', () {
    // teachers.html#history: who declined, the sites asked for and what was
    // approved, tamper signals and laptops that dropped out.
    final detail = demoPastSessionDetail();
    final kinds = detail.recentEvents.map((e) => e.kind).toSet();
    expect(
      kinds,
      containsAll(<String>[
        'JoinDeclined',
        'UnblockRequest',
        'UnblockApproved',
        'TamperDetected',
        'HeartbeatLost',
      ]),
    );
    expect(
      detail.summaries.map((s) => s.kind).toSet(),
      containsAll(kinds),
      reason: 'every kind in the event log is counted in the summary too',
    );
    expect(detail.grants.map((g) => g.host), contains('docs.google.com'));
  });
}
