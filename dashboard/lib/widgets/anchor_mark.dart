import 'package:flutter/material.dart';
import 'package:plink_design_system/plink_design_system.dart';

/// The Anchor mark — "anchor-from-the-ping" (design/ANCHOR_BRAND.md §3).
///
/// Painted natively from the canonical 56×56 viewBox geometry rather than
/// loaded as an SVG so it scales crisply and picks up the live product accent
/// without a new asset/dependency pipeline. The open ring is the Plink ping's
/// shackle, its filled centre the shackle pin; a hairline stem drops through a
/// short stock to a calm fluke arc.
///
/// Drawn in the per-product accent (the only colour besides the wordmark the
/// accent is allowed to carry, §2) — deep indigo on the paper dashboard.
class AnchorMark extends StatelessWidget {
  const AnchorMark({super.key, this.size = 28, this.color});

  /// Square edge length in logical pixels. The brand floor is 16px (§3).
  final double size;

  /// Override the stroke colour; defaults to the theme's product accent.
  final Color? color;

  @override
  Widget build(BuildContext context) {
    final Color stroke = color ?? PlinkProductAccent.of(context).accent;
    return SizedBox.square(
      dimension: size,
      child: CustomPaint(painter: _AnchorMarkPainter(stroke)),
    );
  }
}

class _AnchorMarkPainter extends CustomPainter {
  _AnchorMarkPainter(this.color);

  final Color color;

  @override
  void paint(Canvas canvas, Size size) {
    // The geometry is authored against a 56-unit viewBox (anchor-mark.svg);
    // scale uniformly to whatever box we're given.
    final double s = size.width / 56.0;
    canvas.save();
    canvas.scale(s);

    final Paint stroke = Paint()
      ..color = color
      ..style = PaintingStyle.stroke
      ..strokeWidth = 2.5
      ..strokeCap = StrokeCap.round
      ..strokeJoin = StrokeJoin.round;
    final Paint fill = Paint()..color = color;

    // shackle = the open ping ring
    canvas.drawCircle(const Offset(28, 13), 7, stroke);
    // ping centre / shackle pin
    canvas.drawCircle(const Offset(28, 13), 1.6, fill);
    // stock (crossbar)
    canvas.drawLine(const Offset(17, 24), const Offset(39, 24), stroke);
    // stem
    canvas.drawLine(const Offset(28, 20), const Offset(28, 46), stroke);
    // crown / arms — the calm fluke arc
    final Path fluke = Path()
      ..moveTo(12, 38)
      ..quadraticBezierTo(28, 54, 44, 38);
    canvas.drawPath(fluke, stroke);
    // fluke tips
    canvas.drawLine(const Offset(12, 38), const Offset(9.5, 33), stroke);
    canvas.drawLine(const Offset(44, 38), const Offset(46.5, 33), stroke);

    canvas.restore();
  }

  @override
  bool shouldRepaint(_AnchorMarkPainter oldDelegate) =>
      oldDelegate.color != color;
}

/// The horizontal Anchor lockup — mark + the lowercase **anchor** wordmark
/// (design/ANCHOR_BRAND.md §4). The mark carries the indigo accent; the
/// wordmark stays ink (never indigo), set in Fraunces to match the "plink labs"
/// lockup convention.
class AnchorLockup extends StatelessWidget {
  const AnchorLockup({super.key, this.height = 28});

  /// Mark edge length; the wordmark is sized relative to it (the SVG sets the
  /// wordmark at ~0.6× the 56-unit mark height).
  final double height;

  @override
  Widget build(BuildContext context) {
    final double wordSize = height * 0.62;
    return Semantics(
      label: 'Anchor',
      // The wordmark below already spells it out; flag the glyph row as an
      // image so a screen reader reads "Anchor" once, not the painted mark.
      child: ExcludeSemantics(
        child: Row(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.center,
          children: <Widget>[
            AnchorMark(size: height),
            SizedBox(width: height * 0.28),
            Text(
              'anchor',
              style: TextStyle(
                fontFamily: PlinkType.displayFamily,
                package: PlinkType.fontPackage,
                fontFamilyFallback: PlinkType.displayFallback,
                fontSize: wordSize,
                // Fraunces is variable; select the lockup weight on the wght
                // axis (the bundled axis covers 300–650).
                fontVariations: const <FontVariation>[
                  FontVariation('wght', 560),
                ],
                letterSpacing: PlinkType.tracking(
                  PlinkType.displayTracking,
                  wordSize,
                ),
                color: PlinkColors.ink,
                height: 1.0,
              ),
            ),
          ],
        ),
      ),
    );
  }
}
