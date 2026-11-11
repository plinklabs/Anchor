# Anchor extension

Edge (Chromium) Manifest V3 extension that runs alongside `FocusAgent` on each
student laptop. During an active focus session it observes the active tab URL,
redirects off-allowlist pages to a friendly block page, and hands each block to
the agent, which reports it to the teacher. It stops filtering when the session
ends, and also when the student leaves or declines it in the agent: their
blocked tabs go back to their pages, and off-list sites load again (#462).

**The agent is the extension's only identity (#489).** The extension doesn't
sign in and has no backend connection. The on-box agent, signed in silently with
the student's Windows account, hands down the session it joined over the
native-messaging witness link, and reports what the extension sends back with
its own connection. So the extension filters for exactly the session the student
is in, whatever account Edge is signed in with; there is no browser sign-in a
student could break. See [The agent link](#the-agent-link-146-489).

Design rationale lives in [focus-system-design.md](../focus-system-design.md)
§6 (extension scope) and §11 (Phase 3 build order).

## Status

Phase 3, v1 — URL filter + block page (#72) layered on top of the scaffold
from #71. The extension caches the allowlist of the session the agent hands it,
redirects off-list navigations to the bundled block page, and hands each block
to the agent, which reports it as a `BlockedUrl` event. "Request access" on the
block page goes to the teacher the same way (#73).

Out of scope for v1 (separate issues):

- Path-level filtering (no `/embed/...` allow rules).

## Layout

```
extension/
├── package.json
├── tsconfig.json
├── rollup.config.mjs
├── src/
│   ├── manifest.json         — MV3 manifest (permissions, background SW)
│   ├── background.ts         — service worker; navigation filter + agent link
│   ├── _locales/             — chrome.i18n catalogues (en source + nl)
│   │   ├── en/messages.json
│   │   └── nl/messages.json
│   ├── content/
│   │   ├── block-page.html   — friendly block page
│   │   └── block-page.ts     — block-page script (Go back / Request access)
│   └── shared/
│       ├── i18n.ts           — chrome.i18n seam (t() + localizeDocument)
│       ├── host-matcher.ts   — pure URL/host allowlist matcher
│       ├── host-matcher.test.ts — vitest unit tests for the matcher
│       ├── logger.ts         — thin console wrapper, prefixed scope
│       ├── session-state.ts  — chrome.storage.session helpers
│       ├── types.ts          — wire DTOs of the session the agent hands down
│       └── witness.ts        — native-messaging link to the agent (#146, #489)
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

### Versioning

`package.json` is the **single source of truth** for the extension version
(#208). The build stamps its `version` into the manifest copied to `dist/`, so
the shipped `dist/manifest.json` can never drift from `package.json` — there's
only one number to bump. The committed `src/manifest.json` keeps a mirror that
`manifest.test.ts` locks (so an editor reading the unbuilt source isn't misled).
To release, bump `version` in `package.json` and rebuild. The agent versions
independently from its own single source (`agent/Directory.Build.props`).

## Package & release (Edge Add-ons)

```powershell
npm run package        # build dist/ → artifacts/anchor-extension-<version>.zip
```

`npm run package` ([`scripts/pack-extension.mjs`](scripts/pack-extension.mjs))
builds `dist/` and zips it into `artifacts/anchor-extension-<version>.zip` — the
ZIP the Edge Add-ons store consumes. It is dependency-free (a small built-in ZIP
writer, [`scripts/zip.mjs`](scripts/zip.mjs)) so it runs identically on any OS,
and it refuses to package a build whose manifest version drifts from
`package.json` or that is missing the pinned stable-ID `key`.
[`scripts/pack-extension.test.ts`](scripts/pack-extension.test.ts) drives the
real build + package and reads the produced ZIP back to lock all three
invariants.

**Release convention** (mirrors the agent's, #209): bump `version` in
`package.json`, commit, then push a matching `extension-v<version>` tag. The tag
triggers [`.github/workflows/extension-release.yml`](../.github/workflows/extension-release.yml),
which builds, packages, uploads the ZIP as a workflow artifact, and — when the
submission secrets are configured — publishes/updates the **single canonical**
Edge Add-ons listing. `pack-extension.mjs` cross-checks the tag's version against
the committed `package.json` and fails loudly on a mismatch, so a tag can't ship a
surprising number.

### One canonical listing

There is **one** Anchor listing on the Edge Add-ons store, owned by Plink Labs,
with a single store-assigned extension ID (see the caveat below and **Stable
extension ID**). Because the extension is backend-agnostic — it gets its
backend URL from the on-box agent at runtime (#204) — every fork reuses that one
listing instead of publishing near-identical copies. So **forks normally don't
run this workflow**; it ships the canonical listing.

> **The store assigns the published ID — the committed `key` does not survive a
> store upload.** The Edge Add-ons store rejects any manifest carrying a `key`
> ("The manifest shouldn't contain the key field"), so `pack-extension.mjs`
> strips it from the upload ZIP. The committed `key` only pins the ID for
> *unpacked / self-hosted* installs (see **Stable extension ID** below). Now that
> the product exists, the committed `key` is the **store listing's own public
> key**, so the unpacked / self-host ID equals the store-assigned ID
> (`dnkimhodjfogjibnbbfdjdapgmmiojio`), and the agent-side references —
> `EdgeExtensionPolicy.ExtensionId`, the witness-host `allowed_origins`, and the
> force-install policy — are pinned to it. See **Post-publish: re-pin the store
> ID** below.

### Publishing to the Edge Add-ons store

One-time developer setup (free; this is the **Edge Add-ons** program, distinct
from the dropped Microsoft Store / Partner Center *app* account):

1. Register for free at the [Microsoft Edge Add-ons developer dashboard](https://partner.microsoft.com/dashboard/microsoftedge/)
   (a Microsoft account; no paid registration, unlike the Store app program).
2. Create the **Anchor** product once, uploading a first package built with
   `npm run package`. Note its **Product ID** — every later submission updates
   this same product, preserving the listing and stable ID.
3. Enable the Add-ons **API** (dashboard → *Publish API*) and create API
   credentials: a **Client ID** and an **API key**. These authenticate the
   automated submission.
4. Configure them as repository Actions config so the release workflow can
   submit automatically:

   - variable `EDGE_ADDONS_PRODUCT_ID` — the product ID from step 2 (not a
     secret).
   - secret `EDGE_ADDONS_CLIENT_ID` — the API client ID.
   - secret `EDGE_ADDONS_API_KEY` — the API key.
   - variable `EDGE_ADDONS_KEY_ROTATED` — today's date, so the 72-day expiry can
     be warned about (see below).

   ```powershell
   gh variable set EDGE_ADDONS_PRODUCT_ID  --body "<product-id>"
   gh secret   set EDGE_ADDONS_CLIENT_ID   --body "<client-id>"
   gh secret   set EDGE_ADDONS_API_KEY     --body "<api-key>"
   gh variable set EDGE_ADDONS_KEY_ROTATED --body "$(Get-Date -Format yyyy-MM-dd)"
   ```

#### The API key expires every 72 days

This is the single most common way a release fails to ship. The key does **not**
renew on use, its lifetime **cannot be changed**, and there is **no API to rotate
it** ([upstream request][edge-key-issue], still open). Renew it in Partner Center
→ *Publish API* → **Create API credentials**, then update **both** secrets:

> The renew button regenerates the **Client ID** as well as the key. Updating
> only `EDGE_ADDONS_API_KEY` leaves a mismatched pair, and the publish fails with
> a bare `403` that looks nothing like an expiry. Always set both, then bump
> `EDGE_ADDONS_KEY_ROTATED` to the new date.

[`edge-key-expiry.yml`](../.github/workflows/edge-key-expiry.yml) warns weekly
once the key is inside 14 days of expiry, and a failed publish is diagnosed
automatically ([`diagnose-publish-failure.mjs`](scripts/diagnose-publish-failure.mjs))
so the log says whether to wait out a review or rotate credentials — the two look
identical otherwise. Details: [`docs/RELEASE.md`](../docs/RELEASE.md#rotating-the-edge-add-ons-api-key-every-72-days).

[edge-key-issue]: https://github.com/microsoft/MicrosoftEdge-Extensions/issues/272

If those three are **not** set, the release workflow still builds, packages, and
uploads the ZIP as a workflow artifact, and prints manual-submit instructions —
download the `anchor-extension-<version>` artifact and upload it by hand on the
dashboard. So the listing can be brought up before the API is wired, and the
workflow needs no edit when it is.

No code-signing step is needed here: the Edge store re-signs the package on
publish and assigns the ID. The committed `key` is stripped from the upload (the
store rejects it) and plays no part in the store-published ID — it only fixes the
ID for unpacked / self-hosted installs.

### Post-publish: re-pin the store ID

A store-published extension gets a **store-assigned ID** — different from the ID
the committed `key` would otherwise derive for *unpacked dev* and *self-hosted
`.crx`* installs. After the Edge Add-ons product was created (store ID
`0RDCKF6C9SQ9`, package/CRX ID `dnkimhodjfogjibnbbfdjdapgmmiojio`), every
agent-side reference was re-pinned from the old dev ID to the store ID (#241):

- `EdgeExtensionPolicy.ExtensionId` (`agent/src/FocusAgent.Core/Extension/EdgeExtensionPolicy.cs`)
- the witness-host `allowed_origins` (`agent/src/FocusAgent.WitnessHost/net.anchor.witness.template.json`)
- the force-install policy in **Sideload** / **Agent self-registration** below
- the matching agent + extension tests (`EdgeExtensionPolicyTests`, `e2e/config.ts`, `src/manifest.test.ts`)

So *unpacked dev* installs as the same ID as the store build, the committed `key`
was replaced with the store listing's public key (dashboard → package details) so
`sha256(key)` re-derives the store ID — the pinned-ID value above and the
dev/self-host path now agree.

For a dev iteration loop:

```powershell
npm run watch
```

Rollup rebuilds on file change. Edge picks up changes after clicking **Reload**
on the extension card at `edge://extensions`.

## Localization (i18n)

The extension is localized with Chrome's native **`chrome.i18n`** (#322, part of
the cross-surface effort #320). Catalogues live in
[`src/_locales/<lang>/messages.json`](src/_locales); rollup copies `_locales/`
to `dist/` so they ship at the extension root, and `manifest.json` declares
`"default_locale": "en"`.

- **Locale resolution** is the browser's UI language, chosen automatically by
  the browser. **English (`en`) is the source and fallback**: a key missing from
  the active locale (or an unsupported language) renders the `en` message — never
  a blank or a raw key. `nl` (Dutch) ships as the proof-of-concept locale.
- **How strings resolve.** Static page copy carries a `data-i18n="<key>"`
  attribute and is filled by [`localizeDocument()`](src/shared/i18n.ts) on load;
  dynamic strings (status messages, the idle eyebrow) go through `t('<key>')`.
  Both live in [`src/shared/i18n.ts`](src/shared/i18n.ts), the only seam that
  touches `chrome.i18n`. The inline English text in the HTML mirrors the `en`
  catalogue (so the page reads correctly before scripting runs);
  [`i18n.test.ts`](src/shared/i18n.test.ts) locks the two together so they can't
  drift — the same idea as the manifest/package.json version lock.

### Adding a new locale

1. Copy [`src/_locales/en/messages.json`](src/_locales/en/messages.json) to
   `src/_locales/<lang>/messages.json` (e.g. `de`, `fr-CA`) and translate each
   `message`. Keep the keys and any `placeholders` identical to `en` — leave the
   key out only if there is genuinely nothing to translate (it then falls back to
   English).
2. `npm test` — [`i18n.test.ts`](src/shared/i18n.test.ts) checks the new
   catalogue carries exactly the `en` keys (and matching placeholders), so a
   missing or stray key fails fast.
3. Optionally add an e2e check modelled on
   [`e2e/specs/i18n-dutch.spec.ts`](e2e/specs/i18n-dutch.spec.ts) (launch the
   extension with `loadExtension({ locale: '<lang>' })` and assert the rendered
   copy).

No build wiring changes are needed — rollup copies the whole `_locales/` tree.

## Test

```powershell
npm test
```

Vitest runs the pure-logic unit tests under `src/` (host-matcher, session-state,
tab-scan). These touch no chrome APIs, so they run in plain Node without a
browser shim.

It also runs the release-path tests under `scripts/`: packaging, the publish
diagnostics, and checks on `extension-release.yml` and `edge-key-expiry.yml`.
One of those lints both workflows with
[actionlint](https://github.com/rhysd/actionlint), and is skipped locally when
`actionlint` isn't on `PATH`. CI runs the whole suite, with a pinned actionlint,
on every PR that touches `extension/` or those workflows
([`extension-ci.yml`](../.github/workflows/extension-ci.yml), #412); there a
missing actionlint fails the run instead of skipping.

## End-to-end tests

```powershell
npm run e2e          # build + run the Playwright suite (headed Edge)
npm run e2e:report   # open the last HTML report
```

`npm run e2e` drives the **real** unpacked extension in Edge, connected through
the **real** native-messaging host to a stand-in for the on-box agent — no mocked
`chrome`, no stubbed host. The harness (under [`e2e/`](e2e/)):

1. Starts a fake agent ([`e2e/fake-agent.ts`](e2e/fake-agent.ts)) listening on a
   per-test named pipe. Like the real agent it hands the current session (or
   `no_session`) to every connection, and it records what the extension reports.
2. Builds and registers the real `anchor-witness-host.exe` for the extension
   under test, and loads `dist/` into Edge via a Playwright persistent context
   with the fake agent's pipe in `ANCHOR_WITNESS_PIPE`, so the host Edge starts
   connects to it.
3. Drives the session the way the agent does: `session_started`,
   `allowlist_replaced`, `allowlist_amended`, `session_ended`, and the agent
   going away.

What the agent hands down when the backend says so, and what it sends on, is the
agent e2e's part (`ExtensionSessionHandDownTests`, real agent + real backend +
real host). Together they cover the chain. No backend runs here.

The specs assert real behaviour, and navigate only to loopback hosts (Edge's
`--host-resolver-rules` maps the synthetic test hosts to a local static server,
so no spec touches the public internet):

| Spec | Covers |
| ---- | ------ |
| `extension-loads` | the SW boots in the pinned id and language, and connects to the agent and nothing else (#489) |
| `block-on-start` | a tab already off-list when the session arrives is redirected and reported (#91), also when it arrived while the SW was stopped |
| `block-on-navigation` | off-list navigations are blocked and reported; on-list ones are left untouched (#72); the session stays in force with the agent gone |
| `amend-bundles` | bundle changes re-scan or release tabs (#93, #426); an approved unblock request releases its tab (#73) |
| `restore-on-end` | the end of the session sends blocked tabs back (#121) |
| `end-after-reconnect` | an end the extension missed while its SW was stopped still releases its tabs (#354) |
| `unblock-request` | "Request access" reaches the agent, and its answer — or its absence — shows on the block page (#73) |

**Hermetic against an on-box agent (#332).** On a developer machine the installed
Anchor agent registers the witness native-messaging host *permanently*
(`HKCU\…\Edge\NativeMessagingHosts\net.anchor.witness`), and through it the
extension under test would get that box's real student session. So
`loadExtension()` points that key at the harness's own host for a spec that
brings a fake agent, and at a missing manifest for one that doesn't. Both paths
put the key back to whatever the box had, on close and on process exit, so a run
never leaves your agent unregistered from Edge. `hermetic-witness.spec.ts` drives
that exact situation — an "installed" agent in a session, with the real host
registered for it — and proves it never hears from the extension under test.

**Pinned to English (#364).** The block page and popup render in the browser UI
language (see [Localization](#localization-i18n)), and Edge takes that from the
host's Windows display language unless told otherwise. So `loadExtension()`
always launches Edge with `--lang` set to `DEFAULT_LOCALE` (`en-US`, what the CI
runner has, in [`e2e/config.ts`](e2e/config.ts)), and specs that assert English
copy pass on a Dutch or any other dev box too. A spec that needs another language
asks for it with `loadExtension({ locale: '<lang>' })`, as `i18n-dutch.spec.ts`
does.

**Runs next to the agent's Edge policy (#429).** Once the agent has added Anchor
to `HKLM\SOFTWARE\Policies\Microsoft\Edge\ExtensionInstallForcelist` (#405),
Edge refuses an unpacked `dist/` that claims the forcelisted ID, which the
manifest `key` makes it do, and the launch never returns. So the harness checks
that key under HKLM and HKCU when it loads. If either names Anchor, it says so
once and loads a copy of `dist/` without the `key`, which Edge gives an ID
derived from the copy's path. Every spec runs as usual, except the stable-ID
assertion in `extension-loads.spec.ts`, which is skipped. Neither the policy nor
`dist/` is changed. CI has no such entry and loads `dist/` itself. The check
does nothing off Windows or with a non-Edge `E2E_BROWSER_CHANNEL`.

Prerequisites: Windows (native messaging needs the HKCU host registration),
Node ≥ 22 (the harness is TypeScript run via Node's built-in type stripping),
Microsoft Edge, and the .NET SDK on `PATH` (the harness builds the witness host).
Loading an MV3 extension needs a **headed** browser, so the suite runs headed by
default; CI runs it on a Windows runner with Edge preinstalled
(`.github/workflows/extension-e2e.yml`).

## Dev loop (one command)

```powershell
npm run dev:extension
```

Builds the extension, opens Edge with it connected through the real witness
host to a stand-in agent (the e2e harness's), and drops you into a tiny console
that hands sessions down the way the agent does:

```
s  start a session (Microsoft 365 bundle)
a  amend → drop all bundles (turns open tabs off-list)
m  amend → restore the Microsoft 365 bundle
e  end the current session
q  quit (closes Edge)
```

With a session running, open `https://reddit.com` → block page; open
`https://team.sharepoint.com` → loads. No backend, no dashboard, no agent.
Windows-only, like the host. The witness-host registration is put back when it
quits.

On a box whose Edge policy force-installs Anchor (the agent's HKLM forcelist
entry, #405), Edge refuses `dist/` and the launch would never return. There the
launcher loads a key-less copy of `dist/` instead, the same way the e2e harness
does (#429), says so in one line with the copy's path-derived ID (#431), and
registers the host for that ID.

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

There is nothing to configure: the extension has no settings, no backend URL and
no sign-in. It gets the session from the agent over the witness link (#489). To
see it enforce a real session in dev, run the agent and register the witness
host so Edge can reach it:

```powershell
pwsh scripts/dev/register-witness-host.ps1   # build + HKCU registration (no admin)
```

then start the agent (`scripts/dev/dev-agent.ps1`, see the agent README) and a
session from the dashboard. Without a backend or agent, `npm run dev:extension`
(above) hands sessions down itself.

Extensions before #489 signed the student in through the browser
(`chrome.identity.launchWebAuthFlow`) and connected to the hub themselves, with
the backend URL and Entra config the host handed down (`backend_url`,
`auth_config`). The host still sends those for an extension the store hasn't
updated yet; this extension ignores them.

## Smoke test

With the backend and the agent running, the witness host registered, and the
seeded teacher/student data in place:

1. Open Edge. The extension's service worker logs `witness port connected to
   agent host`, then `agent message: no_session`.
2. Start a session from the dashboard for class `3A` with the **Microsoft 365**
   bundle selected, and confirm it in the agent.
3. The service worker logs `agent message: session_started` followed by
   `active session cached`.
4. Open a new tab and visit `https://outlook.office.com` — loads normally.
5. Open another tab and visit `https://reddit.com` — the tab redirects to the
   block page with the blocked URL displayed. The dashboard event feed receives
   a `BlockedUrl` event within ~1 second.
6. End the session from the dashboard. The service worker logs `active session
   cleared`. Any URL loads normally again.

## Tamper detection

Soft enforcement (design §5.4): we can't stop a student reconfiguring or
sidestepping the extension via Edge's own `edge://extensions` page — no extension
can hide that page, and Anchor deliberately uses **no enterprise policy** (it's
BYOD). Instead the extension makes tampering **visible to the teacher**: during an
active session it hands a tamper signal to the agent, which reports it as a
`TamperDetected` event (an `EventKind` on the backend); the dashboard surfaces it
as a flag on the student's live-roster row (#105). The flag is computed
server-side, so it survives a dashboard reload.

What the extension itself can witness while running:

| Signal | Mechanism | Reported `kind` |
| ------ | --------- | --------------- |
| Site access downgraded ("On all sites" → "On click"/"On specific sites") | `chrome.permissions.onRemoved` | `host_permission_revoked` |
| InPrivate window opened | `chrome.windows.onCreated` (`incognito: true`) | `inprivate_opened` |

InPrivate detection is **best-effort**: the extension only sees incognito windows
once it's been allowed in InPrivate — which is also the case where it still
filters them — so the *reliable* InPrivate signal comes from the agent acting as
an on-box witness (a follow-up, #148).

### The agent link (#146, #489)

The extension opens a native-messaging link to a small host the FocusAgent
registers (`chrome.runtime.connectNative`, see [`witness.ts`](src/shared/witness.ts)
and the [`nativeMessaging`](src/manifest.json) permission). Two things ride on it:

- **The agent witnesses the extension.** While the link is up the agent has a
  live witness; when the extension is disabled or removed the browser tears the
  host down, the agent reports `extension_disabled`, and it pauses Edge while the
  extension stays off during a session (#427). That is the signal the extension
  cannot witness about itself.
- **The agent hands down the session (#489).** `session_started` when the agent
  has joined a session (a declined one never arrives), `allowlist_replaced` and
  `allowlist_amended` as the teacher changes it, `session_ended` when it ends or
  the student leaves it, and on every connect the current session or
  `no_session`. The extension sends back `blocked_url`, `unblock_request`
  (answered with `unblock_request_result`) and `tamper`, which the agent sends
  with its own connection; it also pings the backend for the extension while the
  link is up, so a silent extension still shows (#149).

If the agent goes away mid-session the extension keeps enforcing the session it
has until the agent is back and says otherwise; the teacher sees the agent's
missing heartbeat. Host, pipe protocol, and dev registration:
[`agent/src/FocusAgent.WitnessHost/README.md`](../agent/src/FocusAgent.WitnessHost/README.md).

## Stable extension ID

The extension ID is **pinned**:

```
dnkimhodjfogjibnbbfdjdapgmmiojio
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
  ID (`STABLE_EXTENSION_ID` in [e2e/config.ts](e2e/config.ts)). It runs in CI;
  a machine whose Edge policy force-installs Anchor skips it (#429).

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

Managed devices receive the extension through a Group Policy / Intune
**ExtensionInstallForcelist** entry. For school laptops managed through Intune
that is a configuration profile; [`docs/INTUNE.md`](../docs/INTUNE.md) has the
setting (#458). The registry shape:

```
HKLM\SOFTWARE\Policies\Microsoft\Edge\ExtensionInstallForcelist
  1 = REG_SZ  dnkimhodjfogjibnbbfdjdapgmmiojio;https://edge.microsoft.com/extensionwebstorebase/v1/crx
```

The ID is the pinned `dnkimhodjfogjibnbbfdjdapgmmiojio` (see **Stable extension
ID** above), and the update URL is the Edge Add-ons store's: the same entry the
agent writes itself (below). A self-hosted `.crx` would instead point the update
URL at an `updates.xml` manifest on a local file share or HTTPS endpoint.

For unpacked dev installs the simpler shape is:

```
HKLM\SOFTWARE\Policies\Microsoft\Edge\ExtensionInstallAllowlist
  1 = REG_SZ  dnkimhodjfogjibnbbfdjdapgmmiojio
```

paired with a developer-mode-loaded unpacked extension at a known path.

### Agent self-registration (BYOD, one UAC prompt) — #211, #405

On school laptops managed through Intune, IT pushes the forcelist entry below
with a configuration profile ([`docs/INTUNE.md`](../docs/INTUNE.md), #458), and
the agent finds it and asks nothing. On a student's own, unmanaged laptop there's
no MDM to push it, so the agent sets it up itself (see
`FocusAgent.Core.Extension.EdgeExtensionPolicy` / `ExtensionSelfRegistrar`),
pointed at the canonical Edge Add-ons store:

```
HKLM\SOFTWARE\Policies\Microsoft\Edge\ExtensionInstallForcelist
  <n> = REG_SZ  dnkimhodjfogjibnbbfdjdapgmmiojio;https://edge.microsoft.com/extensionwebstorebase/v1/crx
```

**Why HKLM and a UAC prompt.** Edge also reads the same key under `HKCU`, but a
standard Windows profile can never write it: `HKCU\Software\Policies` gives the
user `ReadKey` only, and just `SYSTEM` and an *elevated* Administrators token have
`FullControl`. The agent runs unelevated (it starts from its per-user `Run`
entry), so even a student who is a local administrator gets "access denied"
there. Builds before #405 tried that HKCU write on every start; it never worked
on a default profile. The agent stays a per-user install, so Velopack installs
and updates stay silent, and asks for elevation **only for this one write**:

1. **Check, without elevation.** At startup the agent reads the forcelist under
   both `HKLM` and `HKCU`. An Anchor entry in either (written by the agent
   earlier, or by Intune/GPO on a managed laptop) means there's nothing to do and
   nothing is asked.
2. **Explain, then one elevated write.** Otherwise the agent first explains the
   step in its own window ("Add Anchor to Edge"). On **Allow**, Windows runs the
   system `reg.exe` elevated (ShellExecute `runas`), which shows **one UAC
   prompt**, and adds Anchor's value at the next free slot `<n>`, never touching
   other entries. The prompt names a Microsoft Windows tool ("Registry Console
   Tool", verified publisher Microsoft Windows): no Anchor code runs elevated. HKLM
   rather than the elevated process's HKCU, because when an administrator types
   their password for a standard user, the elevated process runs as that
   administrator, and its HKCU is theirs, not the student's.
3. **Fallback.** "Not now", a declined prompt, a student who isn't an
   administrator (and has nobody to type the password), or a failed write all fall
   back to the **guided install**: a window that opens Edge at the store listing
   (`GET → Add`). The answer is remembered (`%LOCALAPPDATA%\Anchor\FocusAgent\extension-policy.json`),
   so startup doesn't ask again; the tray menu offers **Set up the Edge
   extension…** for as long as no policy is in place.

Once the policy is in place, the agent counts the extension as there when Edge
has installed it in a profile (its unpacked folder under
`%LOCALAPPDATA%\Microsoft\Edge\User Data\<profile>\Extensions\<id>`, which
the agent reads without Edge running) or when it checks in over the mutual
agent↔extension witness link. The check-in needs a running Edge, and after a
sign-in the agent is usually up long before Edge is, so the grace period for it
(45 s) only starts once Edge runs (#488). If the extension is then still neither
installed nor checked in, the guided install opens after all. On unmanaged
devices Edge honours forcelist entries only for store-hosted extensions; ours is,
so the forcelist also wins over an `ExtensionInstallBlocklist` of `*` that would
block a store install.

**Uninstall.** The Velopack uninstall hook fires-and-forgets the elevated
removal of the HKLM value the agent wrote: `reg.exe delete … /v <n> /f`, again
through ShellExecute `runas`, so it doesn't depend on the app folder Velopack is
about to delete. That's one more UAC prompt during uninstall; the hook waits up
to 20 s for the answer (Velopack stops it at 30 s). The agent only removes the
slot it recorded writing, and only while that slot still holds Anchor's entry, so
an entry Intune or Group Policy manages is never touched (and never costs a
prompt). If the student declines or doesn't answer in time, the entry stays: the
extension stays installed until someone removes the value (re-installing and
uninstalling Anchor again offers the prompt again). Anchor's entries under `HKCU`
(older builds, or IT where that key is writable) are still removed without
elevation.

**"Your browser is managed by your organization."** With any Edge policy set
(HKLM or HKCU, ours or anyone's), Edge shows this line in its settings and menu.
For students and parents: it is Edge's standard notice that *some* setting on the
computer is fixed by a policy. Here the only policy Anchor adds is "keep the
Anchor extension installed". It gives nobody remote control of the browser, and
it doesn't let Anchor read anything beyond what the extension already does during
a focus session. The agent's setup window says this before the UAC prompt, and
removing the value (uninstall) makes the notice go away unless other policies are
set.

**Testing.** No test or dev run ever shows a real UAC prompt (it would take over
the secure desktop with nobody there to answer it). The elevated write sits
behind `IElevatedPolicyWriter`; only a user-facing Release launch (Production,
no dev/test flag) composes the real `reg.exe` writer. Unit tests fake it, and the
agent e2e (`ExtensionPolicySetupTests`) runs the real startup against a throwaway
HKCU subtree with the UAC answer simulated, a throwaway Edge `User Data` folder,
and a stand-in process playing Edge. The real prompt needs a one-time
manual check on a real laptop: install → prompt → value under HKLM → Edge
installs the extension; uninstall → prompt → value removed.
