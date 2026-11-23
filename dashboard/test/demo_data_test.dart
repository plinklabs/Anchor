import 'package:anchor_dashboard/demo/demo_data.dart';
import 'package:flutter_test/flutter_test.dart';

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
