import 'package:http/http.dart' as http;

typedef TokenProvider = Future<String?> Function();

class _BearerTokenClient extends http.BaseClient {
  _BearerTokenClient(this._tokenProvider, this._inner);

  final TokenProvider _tokenProvider;
  final http.Client _inner;

  @override
  Future<http.StreamedResponse> send(http.BaseRequest request) async {
    final token = await _tokenProvider();
    if (token != null && token.isNotEmpty) {
      request.headers['Authorization'] = 'Bearer $token';
    }
    return _inner.send(request);
  }

  @override
  void close() {
    _inner.close();
    super.close();
  }
}

class ApiClient {
  ApiClient({required Uri baseUrl, required TokenProvider tokenProvider})
    : _baseUrl = baseUrl,
      _http = _BearerTokenClient(tokenProvider, http.Client());

  final Uri _baseUrl;
  final http.Client _http;

  Uri get baseUrl => _baseUrl;

  Future<http.Response> get(String path) =>
      _http.get(_baseUrl.resolve(path));

  Future<http.Response> post(
    String path, {
    Object? body,
    Map<String, String>? headers,
  }) => _http.post(_baseUrl.resolve(path), body: body, headers: headers);

  Future<http.Response> put(
    String path, {
    Object? body,
    Map<String, String>? headers,
  }) => _http.put(_baseUrl.resolve(path), body: body, headers: headers);

  Future<http.Response> delete(String path) =>
      _http.delete(_baseUrl.resolve(path));

  void close() => _http.close();
}
