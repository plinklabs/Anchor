import 'dart:convert';
import 'dart:io';

/// contracts/rest.json (#393): the REST API as the dashboard relies on it.
/// Backend CI checks that the backend still serves what it says; the tests in
/// test/contract/ check that the dashboard sends and parses it.
///
/// A shape is an object of fields, a list written as `[element]`, or a type:
/// `string`, `number`, `boolean`, `date-time` or an enum's name. A type ending
/// in `?` may be null.
class RestContract {
  RestContract._(this.routes);

  /// Reads the file from the repo root: `flutter test` runs in dashboard/.
  factory RestContract.load() {
    final json =
        jsonDecode(File('../contracts/rest.json').readAsStringSync())
            as Map<String, dynamic>;
    final enums = {
      for (final MapEntry(:key, :value)
          in (json['enums'] as Map<String, dynamic>).entries)
        key: List<Object?>.unmodifiable(value as List),
    };
    final routes = json['routes'] as Map<String, dynamic>;
    return RestContract._({
      for (final MapEntry(:key, :value) in routes.entries)
        key: RestRoute._(key, value as Map<String, dynamic>, enums),
    });
  }

  /// The routes the dashboard calls, by `METHOD path`.
  final Map<String, RestRoute> routes;
}

/// One route the dashboard calls.
class RestRoute {
  RestRoute._(this.key, Map<String, dynamic> json, this._enums)
    : method = key.substring(0, key.indexOf(' ')),
      path = key.substring(key.indexOf(' ') + 1),
      query = {...(json['query'] as List? ?? const []).cast<String>()},
      request = json['request'],
      response = json['response'];

  final String key;
  final String method;

  /// The path template, relative to the API's base URL, e.g.
  /// `classes/{id}/members/{userId}`.
  final String path;

  /// The query parameters the dashboard may send.
  final Set<String> query;

  /// Every field the backend binds from the body, or null for no body.
  final Object? request;

  /// The fields the dashboard reads, or null when it ignores the body.
  final Object? response;

  final Map<String, List<Object?>> _enums;

  /// The path with its parameters filled in by position, `p1`, `p2`, …: the
  /// values the tests pass for them.
  String get pathFilledIn {
    var n = 0;
    return path.replaceAllMapped(RegExp(r'\{[^}]+\}'), (_) => 'p${++n}');
  }

  /// The ways the response may come: every field with a value, every
  /// nullable one null, and each value of every enum in it.
  List<ResponseVariant> get responseVariants {
    final types = _types(response).toList();
    final longestEnum = types
        .map((t) => _enums[_base(t)]?.length ?? 1)
        .fold(1, (a, b) => a > b ? a : b);
    return [
      const ResponseVariant('with every field set', nulls: false, enumValue: 0),
      if (types.any((t) => t.endsWith('?')))
        const ResponseVariant(
          'with every nullable field null',
          nulls: true,
          enumValue: 0,
        ),
      for (var i = 1; i < longestEnum; i++)
        ResponseVariant(
          'with enum value #${i + 1}',
          nulls: false,
          enumValue: i,
        ),
    ];
  }

  /// A response body as the contract allows it.
  Object? responseBody(ResponseVariant variant) =>
      response == null ? null : _sample(response, '', variant);

  /// The response fields the dashboard reads, as paths: `bundles`,
  /// `bundles[].id`; `[].id` for a list's element.
  Set<String> get responseFields => _fields(response, '');

  /// What's wrong with a request body the dashboard sent, against the fields
  /// the backend binds.
  List<String> requestProblems(Object? sent) {
    final problems = <String>[];
    _check(request, sent, 'body', problems);
    return problems;
  }

  Object? _sample(Object? shape, String name, ResponseVariant variant) {
    if (shape is Map<String, dynamic>) {
      return {
        for (final MapEntry(:key, :value) in shape.entries)
          key: _sample(value, key, variant),
      };
    }
    if (shape is List) return [_sample(shape.single, name, variant)];
    final type = shape as String;
    if (type.endsWith('?') && variant.nulls) return null;
    switch (_base(type)) {
      case 'string':
        return 'contract-$name';
      case 'date-time':
        // As the backend writes a DateTimeOffset: seven fractional digits.
        return '2026-05-26T09:15:00.1234567+00:00';
      case 'number':
        return 1;
      case 'boolean':
        return true;
    }
    final values = _enumValues(type);
    return values[variant.enumValue % values.length];
  }

  void _check(Object? shape, Object? sent, String path, List<String> out) {
    if (shape is Map<String, dynamic>) {
      if (sent is! Map) {
        out.add('$path: sent ${jsonEncode(sent)}, the backend binds an object');
        return;
      }
      for (final key in sent.keys) {
        if (!shape.containsKey(key)) {
          out.add("$path.$key: sent, but the backend doesn't bind it");
        }
      }
      for (final MapEntry(:key, :value) in shape.entries) {
        if (sent.containsKey(key)) {
          _check(value, sent[key], '$path.$key', out);
        } else if (!(value is String && value.endsWith('?'))) {
          out.add('$path.$key: not sent, but the backend needs it');
        }
      }
      return;
    }
    if (shape is List) {
      if (sent is! List) {
        out.add('$path: sent ${jsonEncode(sent)}, the backend binds a list');
        return;
      }
      for (final element in sent) {
        _check(shape.single, element, '$path[]', out);
      }
      return;
    }
    final type = shape as String;
    if (sent == null) {
      if (!type.endsWith('?')) out.add("$path: sent null, which $type isn't");
      return;
    }
    final ok = switch (_base(type)) {
      'string' => sent is String,
      'date-time' => sent is String && DateTime.tryParse(sent) != null,
      'number' => sent is num,
      'boolean' => sent is bool,
      _ => _enumValues(type).contains(sent),
    };
    if (!ok) out.add("$path: sent ${jsonEncode(sent)}, which isn't a $type");
  }

  List<Object?> _enumValues(String type) =>
      _enums[_base(type)] ??
      (throw StateError('contracts/rest.json has no type named $type'));
}

/// One way a response may come, per the contract.
class ResponseVariant {
  const ResponseVariant(
    this.description, {
    required this.nulls,
    required this.enumValue,
  });

  final String description;

  /// Whether every field that may be null is.
  final bool nulls;

  /// Which value every enum field takes, by position (wrapping around).
  final int enumValue;
}

String _base(String type) =>
    type.endsWith('?') ? type.substring(0, type.length - 1) : type;

/// Every type named in a shape.
Iterable<String> _types(Object? shape) sync* {
  if (shape is Map<String, dynamic>) {
    for (final value in shape.values) {
      yield* _types(value);
    }
  } else if (shape is List) {
    yield* _types(shape.single);
  } else if (shape is String) {
    yield shape;
  }
}

Set<String> _fields(Object? shape, String path) {
  if (shape is Map<String, dynamic>) {
    return {
      for (final MapEntry(:key, :value) in shape.entries) ...{
        _join(path, key),
        ..._fields(value, _join(path, key)),
      },
    };
  }
  if (shape is List) return _fields(shape.single, '$path[]');
  return {};
}

String _join(String path, String key) => path.isEmpty ? key : '$path.$key';
