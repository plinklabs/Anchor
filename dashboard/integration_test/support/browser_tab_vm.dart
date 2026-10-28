import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';

// The VM side of browser_tab.dart: no browser, so it records the title the
// app asks the platform for.

String _title = '';

void followTabTitle(WidgetTester tester) {
  _title = '';
  final messenger = tester.binding.defaultBinaryMessenger;
  messenger.setMockMethodCallHandler(SystemChannels.platform, (call) async {
    if (call.method == 'SystemChrome.setApplicationSwitcherDescription') {
      _title = (call.arguments as Map)['label'] as String;
    }
    return null;
  });
  addTearDown(
    () => messenger.setMockMethodCallHandler(SystemChannels.platform, null),
  );
}

String tabTitle() => _title;

Future<({double peak, double tail})?> renderAlertChime() async => null;
