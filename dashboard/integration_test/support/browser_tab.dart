import 'package:flutter_test/flutter_test.dart';

import 'browser_tab_vm.dart'
    if (dart.library.js_interop) 'browser_tab_web.dart'
    as platform;

/// What the browser tab shows, for the live-session alert e2e (#454).
///
/// In a browser (Dashboard E2E, `flutter drive`) these read the real page:
/// `document.title`, and the alert chime rendered by Web Audio. Run as a VM
/// copy under `flutter test` there's no page, so the title is the one the app
/// last asked for, and there's no chime to render.

/// Starts following the tab title. Call it before the app boots.
void followTabTitle(WidgetTester tester) => platform.followTabTitle(tester);

/// The title the browser tab shows now.
String tabTitle() => platform.tabTitle();

/// Renders the live page's alert chime offline, with the browser's Web Audio,
/// and returns its loudest sample and the loudest in its last 0.3 s. Null
/// where there's no Web Audio (a VM copy).
Future<({double peak, double tail})?> renderAlertChime() =>
    platform.renderAlertChime();
