// Thin wrapper around @azure/msal-browser. Loaded before Flutter starts.
// Exposes window.anchorAuth with init/signIn/signOut/acquireToken/getAccount,
// each returning a Promise so Dart can await via dart:js_interop.
(function () {
  let pca = null;
  let apiScope = null;

  // A stale, day-old cached session can leave acquireTokenSilent stalled on a
  // hidden-iframe renewal that never resolves. Bound the silent path so we fall
  // back to a visible interactive popup promptly instead of hanging (#303).
  const SILENT_TIMEOUT_MS = 8000;

  function withTimeout(promise, ms, message) {
    let timer;
    const timeout = new Promise((_resolve, reject) => {
      timer = setTimeout(() => reject(new Error(message)), ms);
    });
    return Promise.race([promise, timeout]).finally(() => clearTimeout(timer));
  }

  async function init(config) {
    if (pca) return;
    if (!window.msal || !window.msal.PublicClientApplication) {
      throw new Error('msal-browser not loaded');
    }
    apiScope = config.apiScope;
    pca = new window.msal.PublicClientApplication({
      auth: {
        clientId: config.clientId,
        authority: 'https://login.microsoftonline.com/' + config.tenantId,
        redirectUri: window.location.origin,
        postLogoutRedirectUri: window.location.origin,
      },
      cache: { cacheLocation: 'sessionStorage' },
    });
    await pca.initialize();
    // Drain any redirect response (we use popup, but this is harmless).
    await pca.handleRedirectPromise();
  }

  function currentAccount() {
    if (!pca) return null;
    const accounts = pca.getAllAccounts();
    return accounts.length > 0 ? accounts[0] : null;
  }

  function accountToJson(account) {
    if (!account) return null;
    return {
      homeAccountId: account.homeAccountId,
      username: account.username,
      name: account.name || account.username,
      idTokenClaims: account.idTokenClaims || {},
    };
  }

  async function signIn() {
    if (!pca) throw new Error('anchorAuth not initialized');
    const result = await pca.loginPopup({
      scopes: [apiScope],
      prompt: 'select_account',
    });
    return accountToJson(result.account);
  }

  async function signOut() {
    if (!pca) return;
    const account = currentAccount();
    if (!account) return;
    await pca.logoutPopup({ account: account });
  }

  // Silent-only acquisition: no interactive fallback. Used to rehydrate the
  // session on app startup (#302), where a surprise popup would be jarring (and
  // browsers block popups without a user gesture anyway). A failure here means
  // "not silently restorable" — the caller falls back to the /login page.
  async function acquireTokenSilent() {
    if (!pca) throw new Error('anchorAuth not initialized');
    const account = currentAccount();
    if (!account) throw new Error('no account');
    const result = await withTimeout(
      pca.acquireTokenSilent({ scopes: [apiScope], account: account }),
      SILENT_TIMEOUT_MS,
      'silent token acquisition timed out',
    );
    return result.accessToken;
  }

  async function acquireToken() {
    try {
      return await acquireTokenSilent();
    } catch (_silentError) {
      // Any silent failure — interaction required, an expired SSO session, or a
      // timed-out/stalled hidden-iframe renewal — falls back to a visible
      // interactive popup rather than surfacing as a hang. If the popup itself
      // fails, that error propagates to the caller for a clear message.
      const account = currentAccount();
      if (!account) throw new Error('no account');
      const result = await pca.acquireTokenPopup({
        scopes: [apiScope],
        account: account,
      });
      return result.accessToken;
    }
  }

  function getAccount() {
    return accountToJson(currentAccount());
  }

  window.anchorAuth = {
    init,
    signIn,
    signOut,
    acquireToken,
    acquireTokenSilent,
    getAccount,
  };
})();
