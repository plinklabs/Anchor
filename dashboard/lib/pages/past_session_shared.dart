import 'package:flutter/widgets.dart';
import 'package:plink_design_system/plink_design_system.dart';

/// Shared paper primitives for the history archive (AD7, #172): the history
/// list and the past-session review both read in the same muted/archived
/// register — calm ink, hairline rules, mono specs, never the magenta spark.
/// These mirror the live-session treatment (AD4, #169) but factored out so the
/// two archive surfaces stay visually consistent.

/// A full-width 1px instrument rule — the system separates with hairlines,
/// never shadows.
class PastHairline extends StatelessWidget {
  const PastHairline({super.key});

  @override
  Widget build(BuildContext context) {
    return const SizedBox(
      height: PlinkBorders.width,
      child: ColoredBox(color: PlinkColors.hairline),
    );
  }
}

/// A sentence-case Space Mono label — the quiet panel/section headers that read
/// like specs on an instrument, never shouting.
TextStyle pastMonoLabel(Color color) =>
    const TextStyle(
      fontFamily: PlinkType.monoFamily,
      package: PlinkType.fontPackage,
      fontFamilyFallback: PlinkType.monoFallback,
      fontSize: PlinkType.label,
    ).copyWith(
      letterSpacing: PlinkType.tracking(
        PlinkType.labelTrackingTight,
        PlinkType.label,
      ),
      color: color,
      height: 1.3,
    );

/// A tabular-figure mono style for timestamps, durations and codes — the
/// columns line up like a log.
TextStyle pastMonoSpec(Color color, double size) => TextStyle(
  fontFamily: PlinkType.monoFamily,
  package: PlinkType.fontPackage,
  fontFamilyFallback: PlinkType.monoFallback,
  fontSize: size,
  color: color,
  height: 1.3,
  fontFeatures: const <FontFeature>[FontFeature.tabularFigures()],
);

String pastFormatDate(DateTime dt) =>
    '${dt.year.toString().padLeft(4, '0')}-'
    '${dt.month.toString().padLeft(2, '0')}-'
    '${dt.day.toString().padLeft(2, '0')}';

String pastFormatTime(DateTime dt) =>
    '${dt.hour.toString().padLeft(2, '0')}:'
    '${dt.minute.toString().padLeft(2, '0')}';

String pastFormatTimeWithSeconds(DateTime dt) =>
    '${dt.hour.toString().padLeft(2, '0')}:'
    '${dt.minute.toString().padLeft(2, '0')}:'
    '${dt.second.toString().padLeft(2, '0')}';

String pastFormatDuration(Duration d) {
  final h = d.inHours;
  final m = d.inMinutes.remainder(60);
  if (h > 0) return '${h}h ${m}m';
  return '${d.inMinutes}m';
}
