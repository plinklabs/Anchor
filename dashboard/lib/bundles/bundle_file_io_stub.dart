import 'bundle_file_io.dart';

/// Non-web fallback. The dashboard only ships to the browser; this exists so
/// the conditional import in `bundle_file_io.dart` resolves on the Dart VM
/// (unit/widget tests) without dragging `package:web` into VM compilation. It
/// is never reached at runtime — tests inject a fake instead.
BundleFileIo createBundleFileIo() => throw UnsupportedError(
  'Bundle import/export is only available on the web build.',
);
