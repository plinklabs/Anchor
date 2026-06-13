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

Vitest runs the pure-logic unit tests under `src/` (host-matcher, session-state,
tab-scan). These touch no chrome APIs, so they run in plain Node without a
browser shim.

## End-to-end tests

```powershell
npm run e2e          # build + run the Playwright suite (headed Edge)
npm run e2e:report   # open the last HTML report
```

`npm run e2e` drives the **real** unpacked extension in Edge against a **real**
backend — no mocked `chrome`, no stubbed SignalR hub. The harness (under
[`e2e/`](e2e/)):

1. Boots the backend itself (Playwright `webServer` → `node e2e/run-backend.ts`
   → `dotnet run`) in Development on port **5281** with a throwaway, freshly
   seeded SQLite DB under the OS temp dir — never the dev `anchor.dev.db`.
2. Loads `dist/` into Edge via a Playwright persistent context, writes the
   backend URL + the seeded **Dev Student** OID into `chrome.storage.local`, and
   `chrome.runtime.reload()`s so the MV3 service worker re-reads them and
   connects to the live hub.
3. Drives session lifecycle over **REST** with the dev-impersonation header
   (`POST /sessions`, `PUT /sessions/{id}/bundles`, `POST /sessions/{id}/end`) —
   the same wire contract the dashboard uses.

The specs assert real behaviour, and navigate only to loopback hosts (Edge's
`--host-resolver-rules` maps the synthetic test hosts to a local static server,
so no spec touches the public internet):

| Spec | Covers |
| ---- | ------ |
| `extension-loads` | the SW boots, refuses without auth, then connects once configured (spike) |
| `block-on-start` | a tab already off-list when a session starts is redirected (#91) |
| `block-on-navigation` | off-list navigations are blocked; on-list ones are left untouched (#72) |
| `amend-bundles` | dropping a bundle mid-session re-scans and blocks a now-off-list tab (#93) |

Prerequisites: Node ≥ 22 (the harness is TypeScript run via Node's built-in type
stripping), Microsoft Edge, and the .NET SDK on `PATH` (the harness builds and
runs the backend). Loading an MV3 extension needs a **headed** browser, so the
suite runs headed by default; CI runs it on a Windows runner with Edge
preinstalled (`.github/workflows/extension-e2e.yml`).

> Not covered: session-end does **not** restore a blocked tab to its original
> URL (the block page only offers a manual **Go back**), so there is no
> restore-on-end spec — that behaviour isn't implemented in the extension.

## Dev loop (one command)

```powershell
npm run dev:extension
```

Removes the manual multi-process startup: it builds the extension, boots a
seeded backend if one isn't already running (reusing a dev backend on
`http://localhost:5276` if present), opens Edge with the extension preconfigured
as the Dev Student, and drops you into a tiny console:

```
s  start a session (Microsoft 365 bundle)
a  amend → drop all bundles (turns open tabs off-list)
m  amend → restore the Microsoft 365 bundle
e  end the current session
q  quit (closes Edge, stops the backend if it started one)
```

With a session running, open `https://reddit.com` → block page; open
`https://outlook.office.com` → loads. No dashboard, no agent.

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

## Tamper detection

Soft enforcement (design §5.4): we can't stop a student reconfiguring or
sidestepping the extension via Edge's own `edge://extensions` page — no extension
can hide that page, and Anchor deliberately uses **no enterprise policy** (it's
BYOD). Instead the extension makes tampering **visible to the teacher**: during an
active session it reports a `TamperDetected` event (an `EventKind` on the
backend), which the dashboard surfaces as a flag on the student's live-roster row
(#105). The flag is computed server-side, so it survives a dashboard reload.

What the extension itself can witness while running:

| Signal | Mechanism | Reported `kind` |
| ------ | --------- | --------------- |
| Site access downgraded ("On all sites" → "On click"/"On specific sites") | `chrome.permissions.onRemoved` | `host_permission_revoked` |
| InPrivate window opened | `chrome.windows.onCreated` (`incognito: true`) | `inprivate_opened` |
| On-box FocusAgent went away mid-session | native-messaging host relays its pipe to the agent dropped | `agent_unavailable` |

InPrivate detection is **best-effort**: the extension only sees incognito windows
once it's been allowed in InPrivate — which is also the case where it still
filters them — so the *reliable* InPrivate signal comes from the agent acting as
an on-box witness (a follow-up, #148).

### Agent-as-witness link (#146 part 1)

The signal the extension **cannot** witness about itself — being disabled or
removed — comes from the agent. The extension opens a native-messaging link to a
small host the FocusAgent registers (`chrome.runtime.connectNative`, see
[`witness.ts`](src/shared/witness.ts) and the [`nativeMessaging`](src/manifest.json)
permission). While the link is up the agent has a live witness; when the
extension is disabled/removed the browser tears the host down and the agent
reports `extension_disabled`. The reverse — the agent dying — is relayed back to
the extension as `agent_unavailable` (above). Host, pipe protocol, and dev
registration: [`agent/src/FocusAgent.WitnessHost/README.md`](../agent/src/FocusAgent.WitnessHost/README.md).

## Stable extension ID

The extension ID is **pinned**:

```
akkfdaclmpfcnjalcifkcbhgjnnopman
```

Edge/Chrome derive an unpacked extension's ID from its public key. Without a
`key` in the manifest they fall back to the *install path*, so the ID changes
per machine and per load — which a managed-Edge policy (`ExtensionInstallForcelist`
/ `ExtensionInstallAllowlist`) can't pin, because policy keys an extension by
ID. We commit the public key as the `key` field in [src/manifest.json](src/manifest.json),
so loading `dist/` (unpacked) or the packed `.crx` yields the **same ID on every
machine** (#123).

The ID is a pure function of that public key:
`sha256(DER-public-key)`, first 16 bytes, hex, each hex digit `0–f` mapped to
`a–p`. Two checks guard it so a key change can't silently break deployed policy:

- [src/manifest.test.ts](src/manifest.test.ts) re-derives the ID from the committed
  `key` and asserts it equals the value above (runs under `npm test`).
- the `extension-loads` e2e spec asserts a **real Edge load** assigns that exact
  ID (`STABLE_EXTENSION_ID` in [e2e/config.ts](e2e/config.ts)).

### Signing key

The matching **private key signs the packed `.crx`** and must never enter the
repo (`*.pem` / `*.crx` are gitignored). It is kept in two places:

- **Authoritative copy:** offline in the school's secret store — the source of
  truth, never on a dev machine.
- **CI:** a GitHub Actions secret `EDGE_EXTENSION_PRIVATE_KEY` (PEM), consumed by
  the future `.crx` packaging/signing workflow. Set it with
  `gh secret set EDGE_EXTENSION_PRIVATE_KEY --body "<pem>"` (pass `--body`; never
  pipe — PowerShell appends a CRLF that corrupts the secret).

**Regenerating the key changes the ID** and invalidates every deployed policy
entry, so treat it as a last resort. If you must, regenerate the keypair, replace
`manifest.json` `key` with the new base64 SPKI public key, and update the ID in
this file, `src/manifest.test.ts`, and `e2e/config.ts` together (the tests will
fail until all three agree). The public key + ID were produced with Node:

```js
const { generateKeyPairSync, createHash } = require('node:crypto');
const { publicKey, privateKey } = generateKeyPairSync('rsa', {
  modulusLength: 2048,
  publicKeyEncoding: { type: 'spki', format: 'der' },
  privateKeyEncoding: { type: 'pkcs8', format: 'pem' },
});
const key = publicKey.toString('base64');               // → manifest `key`
const id = createHash('sha256').update(publicKey).digest()
  .subarray(0, 16).toString('hex').split('')
  .map((c) => String.fromCharCode(97 + parseInt(c, 16))).join(''); // → extension ID
// privateKey → offline store + EDGE_EXTENSION_PRIVATE_KEY (never committed)
```

## Sideload (production install path)

Once the extension ships, managed devices will receive it via a Group Policy /
Intune **ExtensionInstallForcelist** entry. The registry shape (documented
here for future reference — not implemented in this scaffold):

```
HKLM\SOFTWARE\Policies\Microsoft\Edge\ExtensionInstallForcelist
  1 = REG_SZ  akkfdaclmpfcnjalcifkcbhgjnnopman;<update-url>
```

The ID is the pinned `akkfdaclmpfcnjalcifkcbhgjnnopman` (see **Stable extension
ID** above). `<update-url>` points at an `updates.xml` manifest hosted on a local
file share or HTTPS endpoint and referencing the packed `.crx`.

For unpacked dev installs the simpler shape is:

```
HKLM\SOFTWARE\Policies\Microsoft\Edge\ExtensionInstallAllowlist
  1 = REG_SZ  akkfdaclmpfcnjalcifkcbhgjnnopman
```

paired with a developer-mode-loaded unpacked extension at a known path.

Actual registry-script generation is a follow-up issue once the extension ID
is pinned and we've decided whether to host the `.crx` ourselves or publish
to the Edge Add-ons private listing (see [focus-system-design.md](../focus-system-design.md) §6).
