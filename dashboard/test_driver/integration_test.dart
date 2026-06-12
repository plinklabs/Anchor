import 'package:integration_test/integration_test_driver.dart';

// Host driver for `flutter drive` (web / headless Chrome). The real test body
// lives in integration_test/; this just relays its results back to the runner.
Future<void> main() => integrationDriver();
