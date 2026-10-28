import 'dart:js_interop';
import 'dart:js_interop_unsafe';
import 'dart:math' as math;

import 'package:anchor_dashboard/alerts/alert_platform_web.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:web/web.dart' as web;

// The browser side of browser_tab.dart: the real page.

void followTabTitle(WidgetTester tester) {}

String tabTitle() => web.document.title;

Future<({double peak, double tail})?> renderAlertChime() async {
  const int rate = 44100;
  final context = web.OfflineAudioContext(1.toJS, rate, rate);
  WebAlertSound.scheduleChime(context);
  final web.AudioBuffer buffer = await context.startRendering().toDart;
  final samples = buffer.getChannelData(0).toDart;
  double loudest(Iterable<double> of) =>
      of.fold(0.0, (max, s) => math.max(max, s.abs()));
  return (
    peak: loudest(samples),
    tail: loudest(samples.skip(samples.length - (rate * 0.3).round())),
  );
}

String? notificationPermission() =>
    globalContext.has('Notification') ? web.Notification.permission : null;
