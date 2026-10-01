import 'dart:ui' show Locale;

import 'package:integration_test/integration_test.dart';

/// The language every dashboard e2e runs the app in unless a test asks for
/// another: US English, what CI's ubuntu runner gives headless Chrome (#371).
const Locale defaultE2eLocale = Locale('en', 'US');

/// Initializes the integration-test binding and pins the language the app sees
/// to [locales], [defaultE2eLocale] unless a test asks for another (#371).
///
/// The dashboard follows the browser language (#321), and `flutter drive`
/// starts Chrome with no `--lang`, so Chrome reports the host's display
/// language. The en-US CI runner rendered English, but on a Dutch machine the
/// app rendered Dutch and every English assertion failed. The pin replaces
/// what the browser reports for the whole file, so the app still picks its
/// locale through `localeResolutionCallback` in `main.dart`, the path a real
/// browser takes, just from a fixed language list.
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
  return binding;
}
