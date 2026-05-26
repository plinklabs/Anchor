# Anchor extension

Edge (Chromium) Manifest V3 extension that runs alongside `FocusAgent` on each
student laptop. During an active focus session it observes the active tab URL,
redirects off-allowlist pages to a friendly block page, and reports the block
back to the backend over SignalR.

Design rationale lives in [focus-system-design.md](../focus-system-design.md)
§6 (extension scope) and §11 (Phase 3 build order).

## Status

Phase 3, v1 — URL filter + block page (#72) layered on top of the scaffold
from #71. The extension joins a live SignalR session, caches the allowlist,
redirects off-list navigations to the bundled block page, and reports each
block as a `BlockedUrl` event.

Out of scope for v1 (separate issues):

- "Request access" button wiring (the button is rendered but disabled).
- Path-level filtering (no `/embed/...` allow rules).
- Production Entra auth via `chrome.identity` — dev uses the impersonation
  fallback documented below.

## Layout

```
extension/
├── package.json
├── tsconfig.json
├── rollup.config.mjs
├── src/
│   ├── manifest.json         — MV3 manifest (permissions, background SW)
│   ├── background.ts         — service worker; navigation filter + hub client
│   ├── content/
│   │   ├── block-page.html   — friendly block page
│   │   └── block-page.ts     — block-page script (Go back / Request access)
│   └── shared/
│       ├── host-matcher.ts   — pure URL/host allowlist matcher
│       ├── host-matcher.test.ts — vitest unit tests for the matcher
│       ├── hub-client.ts     — @microsoft/signalr wrapper
│       ├── logger.ts         — thin console wrapper, prefixed scope
│       ├── session-state.ts  — chrome.storage.session helpers
│       ├── settings.ts       — chrome.storage.local config reader
│       └── types.ts          — wire DTOs mirroring backend payloads
└── dist/                     — build output (gitignored), the unpacked extension
```

## Prerequisites

- [Node.js LTS](https://nodejs.org/) (≥ 20)
- Microsoft Edge (Chromium-based)

## Build

```powershell
cd extension
npm install
npm run build
```

Output lands in `extension/dist/` — that directory is the unpacked extension.

For a dev iteration loop:

```powershell
npm run watch
```

Rollup rebuilds on file change. Edge picks up changes after clicking **Reload**
on the extension card at `edge://extensions`.

## Test

```powershell
npm test
```

Vitest runs the host-matcher unit tests (exact / wildcard / suffix / chrome
internals / malformed inputs). The matcher is pure — no chrome APIs touched —
so it runs in plain Node without a browser shim.

## Dev load

1. Open `edge://extensions` in Edge.
2. Toggle **Developer mode** on (bottom-left).
3. Click **Load unpacked** and select `extension/dist/`.
4. The extension appears as **Anchor**. The background service worker should
   log `service worker started` — open it via **Inspect views: service worker**
   on the extension card to see the console.

After `npm run build` or `npm run watch` rebuilds, **reload the extension**
so Edge picks up the new bundle. The reliable way is to open the extension's
detail page (`edge://extensions/?id=<extension-id>`) and refresh it (Ctrl+R);
the small reload icon on the card in the list view does not always pick up
unpacked-extension changes.

## Configure (dev)

The extension reads two settings from `chrome.storage.local`:

| Key                  | Default                 | What it does |
| -------------------- | ----------------------- | ------------ |
| `backendUrl`         | `http://localhost:5276` | Base URL of the Anchor backend. No trailing slash. |
| `devImpersonateOid`  | _(none)_                | Dev-only impersonation OID. Sent as the `dev_impersonate_oid` query parameter on the SignalR hub URL so the backend can authenticate without a real Entra token. |

Without `devImpersonateOid` set, the extension refuses to connect to the hub —
production Entra auth via `chrome.identity` is a follow-up issue, and spinning
in a 401 loop is worse than a clean refuse.

To set the values for dev:

1. Open `edge://extensions`, click **Inspect views: service worker** on the
   Anchor card. A DevTools window opens, scoped to the background SW.
2. In the **Console** tab, paste:

   ```js
   chrome.storage.local.set({
     backendUrl: 'http://localhost:5276',
     devImpersonateOid: '22222222-2222-2222-2222-222222222222'
   })
   ```

   The OID above is the seeded `Dev Student` from [DevDataSeeder](../backend/src/Anchor.Infrastructure/Persistence/DevDataSeeder.cs).
3. Reload the extension (extension card → **Reload**) so the background SW
   re-reads settings on startup.

## Smoke test

With the backend running (`dotnet run --project backend/src/Anchor.Api`) and
the seeded teacher/student data in place:

1. Configure the extension to impersonate the seeded student (see above).
2. Reload the extension and confirm the background SW logs `hub connection established`.
3. Start a session from the dashboard for class `3A` with the **Microsoft 365**
   bundle selected.
4. The background SW should log `SessionStarted received` followed by `active
   session cached`.
5. Open a new tab and visit `https://outlook.office.com` — loads normally.
6. Open another tab and visit `https://reddit.com` — the tab redirects to the
   block page with the blocked URL displayed. The dashboard event feed receives
   a `BlockedUrl` event within ~1 second.
7. End the session from the dashboard. The background SW logs `active session
   cleared`. Any URL loads normally again.

## Sideload (production install path)

Once the extension ships, managed devices will receive it via a Group Policy /
Intune **ExtensionInstallForcelist** entry. The registry shape (documented
here for future reference — not implemented in this scaffold):

```
HKLM\SOFTWARE\Policies\Microsoft\Edge\ExtensionInstallForcelist
  1 = REG_SZ  <extension-id>;<update-url>
```

`<extension-id>` is derived from the extension's public key (set via the
`key` field in `manifest.json` so the ID is stable across machines).
`<update-url>` points at an `updates.xml` manifest hosted on a local file
share or HTTPS endpoint and referencing the packed `.crx`.

For unpacked dev installs the simpler shape is:

```
HKLM\SOFTWARE\Policies\Microsoft\Edge\ExtensionInstallAllowlist
  1 = REG_SZ  <extension-id>
```

paired with a developer-mode-loaded unpacked extension at a known path.

Actual registry-script generation is a follow-up issue once the extension ID
is pinned and we've decided whether to host the `.crx` ourselves or publish
to the Edge Add-ons private listing (see [focus-system-design.md](../focus-system-design.md) §6).
