import 'msal_auth_service_stub.dart'
    if (dart.library.js_interop) 'msal_auth_service_web.dart';
import 'msal_config.dart';

class AccountInfo {
  const AccountInfo({
    required this.homeAccountId,
    required this.username,
    required this.displayName,
    required this.department,
  });

  final String homeAccountId;
  final String username;
  final String displayName;
  final String? department;
}

abstract class MsalAuthService {
  factory MsalAuthService(MsalConfig config) = MsalAuthServiceImpl;

  Future<void> initialize();
  Future<AccountInfo?> signIn();
  Future<void> signOut();
  Future<String> acquireToken();

  /// Acquires an access token *silently only* — no interactive popup fallback.
  /// Used to rehydrate the session on app startup (#302): a boot-time popup
  /// would be jarring (and browsers block popups without a user gesture), so a
  /// silent failure here just means "send the user to /login" rather than
  /// ambushing them with an account picker. [acquireToken] is the interactive
  /// path used per-request and for explicit sign-in.
  Future<String> acquireTokenSilent();

  AccountInfo? currentAccount();
}
