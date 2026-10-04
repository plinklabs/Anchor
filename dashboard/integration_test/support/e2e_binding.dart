import 'dart:ui' show Locale, Size;

import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';

/// The language every dashboard e2e runs the app in unless a test asks for
/// another: US English, what CI's ubuntu runner gives headless Chrome (#371).
const Locale defaultE2eLocale = Locale('en', 'US');

/// The window every dashboard e2e test lays the app out in, in logical pixels
/// at a device pixel ratio of 1, unless the test sets its own size (#376).
const Size defaultE2eViewSize = Size(1400, 1000);

/// Initializes the integration-test binding, pins the language the app sees
/// to [locales] ([defaultE2eLocale] unless a test asks for another, #371), and
/// starts every test in the file at [defaultE2eViewSize] (#376).
///
/// The dashboard follows the browser language (#321), and `flutter drive`
/// starts Chrome with no `--lang`, so Chrome reports the host's display
/// language. The en-US CI runner rendered English, but on a Dutch machine the
/// app rendered Dutch and every English assertion failed. The pin replaces
/// what the browser reports for the whole file, so the app still picks its
/// locale through `localeResolutionCallback` in `main.dart`, the path a real
/// browser takes, just from a fixed language list.
///
/// The window size has the same problem. `flutter drive` sizes Chrome to
/// 1600x1024 (its `--browser-dimension` default), but other runs get their
/// own window: 800x600 under `flutter test`, where 5 tests that set no size
/// failed (#376). So every test starts at [defaultE2eViewSize] wherever it
/// runs, and the real window comes back when it ends. A test that needs
/// another size sets `tester.view.physicalSize` itself, as
/// `classes_scope_row_test.dart` does for its narrow window.
///
/// Every integration test calls this instead of
/// `IntegrationTestWidgetsFlutterBinding.ensureInitialized()`;
/// `test/e2e_binding_usage_test.dart` keeps it that way. A test that needs
/// another language passes it, as `dutch_locale_test.dart` does.
IntegrationTestWidgetsFlutterBinding ensureE2eBinding({
  List<Locale> locales = const <Locale>[defaultE2eLocale],
}) {
  final binding = IntegrationTestWidgetsFlutterBinding.ensureInitialized();
  binding.platformDispatcher.localesTestValue = locales;
  setUp(() {
    final view = binding.platformDispatcher.implicitView!;
    view.physicalSize = defaultE2eViewSize;
    view.devicePixelRatio = 1.0;
    addTearDown(view.resetPhysicalSize);
    addTearDown(view.resetDevicePixelRatio);
  });
  return binding;
}
