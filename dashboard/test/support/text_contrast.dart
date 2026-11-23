import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:plink_design_system/plink_design_system.dart';

/// WCAG 2.x relative luminance of an opaque colour.
double relativeLuminance(Color color) {
  double channel(double v) =>
      v <= 0.03928 ? v / 12.92 : math.pow((v + 0.055) / 1.055, 2.4).toDouble();
  return 0.2126 * channel(color.r) +
      0.7152 * channel(color.g) +
      0.0722 * channel(color.b);
}

/// WCAG 2.x contrast ratio between two opaque colours, from 1 to 21.
double contrastRatio(Color a, Color b) {
  final double la = relativeLuminance(a);
  final double lb = relativeLuminance(b);
  return (math.max(la, lb) + 0.05) / (math.min(la, lb) + 0.05);
}

/// One piece of on-screen text and the contrast it gets against what it sits
/// on.
class TextContrast {
  const TextContrast({
    required this.text,
    required this.color,
    required this.background,
    required this.ratio,
    required this.required,
  });

  final String text;
  final Color color;
  final Color background;
  final double ratio;

  /// 4.5 for normal text, 3 for large text (WCAG 1.4.3).
  final double required;

  bool get passes => ratio >= required;

  @override
  String toString() =>
      '"$text": ${_hex(color)} on ${_hex(background)} is '
      '${ratio.toStringAsFixed(2)}:1, needs $required:1';
}

/// Every piece of text on screen, each with the contrast it gets against the
/// surface it is painted on (#601).
///
/// The surface is found by walking up from the text to the nearest opaque
/// fill: a [ColoredBox], a [DecoratedBox] or [Ink] with a colour, or a
/// [Material] (a page's scaffold, a dialog, a filled button). Translucent
/// fills on the way are blended over it, and an [Opacity] or fade on the way
/// fades the text.
///
/// Left out, because WCAG 1.4.3 or the design system answers for them:
/// - an [Icon]'s glyph, which is not text (1.4.11 asks 3:1 of the icons a
///   screen can't be understood without);
/// - text in a disabled control, which WCAG exempts;
/// - an input's placeholder in the caption grey, and the magenta labels of a
///   [TextButton] and a spark [PlinkBadge]: the Plink theme and badge paint
///   those, so they are fixed in the design system, not here
///   (plinklabs/plink-design-system#20 and #21).
List<TextContrast> textContrasts(WidgetTester tester) {
  final List<TextContrast> found = <TextContrast>[];
  for (final Element element in tester.elementList(find.byType(RichText))) {
    final RichText paragraph = element.widget as RichText;
    final String plain = paragraph.text.toPlainText();
    final _Surface surface = _surfaceOf(element, plain);
    if (surface.exempt || surface.opacity == 0) continue;
    _visit(paragraph.text, const TextStyle(), (String text, TextStyle style) {
      final Color? ink = style.color;
      if (ink == null || style.foreground != null) return;
      if (ink == surface.upstreamColor) return;
      final Color faded = ink.withValues(alpha: ink.a * surface.opacity);
      final Color painted = Color.alphaBlend(faded, surface.color);
      final double size = paragraph.textScaler.scale(style.fontSize ?? 14);
      final bool bold = (style.fontWeight ?? FontWeight.normal).value >= 700;
      final bool large = size >= 24 || (size >= 18.66 && bold);
      found.add(
        TextContrast(
          text: text.trim(),
          color: painted,
          background: surface.color,
          ratio: contrastRatio(painted, surface.color),
          required: large ? 3 : 4.5,
        ),
      );
    });
  }
  return found;
}

/// Fails when any text on screen falls short of WCAG AA against its surface.
///
/// Pass [shows] to name text the screen must be showing, so the check can't
/// pass because the text it is about never rendered.
void expectReadableText(
  WidgetTester tester, {
  List<String> shows = const <String>[],
}) {
  final List<TextContrast> all = textContrasts(tester);
  for (final String text in shows) {
    expect(
      all.where((TextContrast t) => t.text.contains(text)),
      isNotEmpty,
      reason: '"$text" should be on screen for the contrast check',
    );
  }
  final List<TextContrast> failing = all
      .where((TextContrast t) => !t.passes)
      .toList();
  if (failing.isNotEmpty) {
    fail(
      'text below WCAG AA (#601):\n'
      '${failing.map((TextContrast t) => '  $t').join('\n')}',
    );
  }
}

void _visit(
  InlineSpan span,
  TextStyle inherited,
  void Function(String text, TextStyle style) onText,
) {
  if (span is! TextSpan) return;
  final TextStyle style = inherited.merge(span.style);
  final String? text = span.text;
  if (text != null && text.trim().isNotEmpty) onText(text, style);
  for (final InlineSpan child in span.children ?? const <InlineSpan>[]) {
    _visit(child, style, onText);
  }
}

class _Surface {
  const _Surface(
    this.color,
    this.opacity, {
    this.exempt = false,
    this.upstreamColor,
  });
  final Color color;
  final double opacity;
  final bool exempt;

  /// The colour the design system gives this text, if it is text the design
  /// system colours itself ([_upstreamColorOf]).
  final Color? upstreamColor;
}

_Surface _surfaceOf(Element element, String text) {
  final List<Color> fills = <Color>[];
  double opacity = 1;
  bool exempt = false;
  Color? upstreamColor;
  element.visitAncestorElements((Element ancestor) {
    final Widget widget = ancestor.widget;
    if (widget is Icon || _isDisabledControl(widget)) {
      exempt = true;
      return false;
    }
    upstreamColor ??= _upstreamColorOf(widget, text);
    if (widget is Opacity) opacity *= widget.opacity;
    if (widget is AnimatedOpacity) opacity *= widget.opacity;
    if (widget is FadeTransition) opacity *= widget.opacity.value;
    final Color? fill = _fillOf(ancestor);
    if (fill != null && fill.a > 0) {
      fills.add(fill);
      if (fill.a >= 1) return false;
    }
    return true;
  });
  // Nothing opaque above it: the page itself, which is paper.
  Color color = PlinkColors.paper;
  for (final Color fill in fills.reversed) {
    color = Color.alphaBlend(fill, color);
  }
  return _Surface(color, opacity, exempt: exempt, upstreamColor: upstreamColor);
}

Color? _fillOf(Element element) {
  final Widget widget = element.widget;
  if (widget is ColoredBox) return widget.color;
  if (widget is DecoratedBox &&
      widget.position == DecorationPosition.background) {
    return _decorationColor(widget.decoration);
  }
  if (widget is Ink) return _decorationColor(widget.decoration);
  if (widget is Material) {
    if (widget.color != null) return widget.color;
    final ThemeData theme = Theme.of(element);
    return switch (widget.type) {
      MaterialType.canvas => theme.canvasColor,
      MaterialType.card => theme.cardColor,
      _ => null,
    };
  }
  return null;
}

Color? _decorationColor(Decoration? decoration) => switch (decoration) {
  BoxDecoration(:final Color? color) => color,
  ShapeDecoration(:final Color? color) => color,
  _ => null,
};

bool _isDisabledControl(Widget widget) => switch (widget) {
  ButtonStyleButton() => !widget.enabled,
  IconButton(:final VoidCallback? onPressed) => onPressed == null,
  InputDecorator(:final InputDecoration decoration) => !decoration.enabled,
  ListTile(:final bool enabled) => !enabled,
  _ => false,
};

/// The colour the Plink design system gives text under [widget] when that
/// colour falls short on paper, so it is fixed in the design system rather
/// than worked around here: the theme's muted placeholder, its magenta
/// [TextButton] label, and the spark [PlinkBadge]'s magenta text
/// (plinklabs/plink-design-system#20, #21). Only that exact colour is let
/// through; anything the dashboard colours itself is still checked. Once the
/// design system fixes them and the dashboard takes that version, this goes.
Color? _upstreamColorOf(Widget widget, String text) => switch (widget) {
  InputDecorator(:final InputDecoration decoration)
      when decoration.hintText == text =>
    PlinkColors.muted,
  TextButton() => PlinkColors.magenta,
  PlinkBadge(:final BadgeVariant variant) when variant == BadgeVariant.spark =>
    PlinkColors.magenta,
  _ => null,
};

String _hex(Color color) =>
    '#${(color.toARGB32() & 0xFFFFFF).toRadixString(16).padLeft(6, '0').toUpperCase()}';
