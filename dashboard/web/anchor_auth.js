// Thin wrapper around @azure/msal-browser. Loaded before Flutter starts.
// Exposes window.anchorAuth with init/signIn/signOut/acquireToken/getAccount,
// each returning a Promise so Dart can await via dart:js_interop.
(function () {
  let pca = null;
  let apiScope = null;

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

  async function acquireToken() {
    if (!pca) throw new Error('anchorAuth not initialized');
    const account = currentAccount();
    if (!account) throw new Error('no account');
    try {
      const result = await pca.acquireTokenSilent({
        scopes: [apiScope],
        account: account,
      });
      return result.accessToken;
    } catch (e) {
      if (e && e.name === 'InteractionRequiredAuthError') {
        const result = await pca.acquireTokenPopup({
          scopes: [apiScope],
          account: account,
        });
        return result.accessToken;
      }
      throw e;
    }
  }

  function getAccount() {
    return accountToJson(currentAccount());
  }

  window.anchorAuth = { init, signIn, signOut, acquireToken, getAccount };
})();
