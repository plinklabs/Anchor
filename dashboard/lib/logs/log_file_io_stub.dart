import 'log_file_io.dart';

/// Non-web fallback. The dashboard only ships to the browser; this exists so
/// the conditional import in `log_file_io.dart` resolves on the Dart VM
/// (unit/widget tests) without dragging `package:web` into VM compilation. It
/// is never reached at runtime — tests inject a fake instead.
LogFileIo createLogFileIo() => throw UnsupportedError(
  'Saving log files is only available on the web build.',
);
