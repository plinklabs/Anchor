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
  AccountInfo? currentAccount();
}
