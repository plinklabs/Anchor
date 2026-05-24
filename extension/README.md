# Anchor extension

Edge (Chromium) Manifest V3 extension that runs alongside `FocusAgent` on each
student laptop. During a focus session it observes the active tab URL,
redirects off-allowlist pages to a friendly block page, and reports activity
back to the backend over SignalR.

Design rationale lives in [focus-system-design.md](../focus-system-design.md)
§6 (extension scope) and §11 (Phase 3 build order).

## Status

Phase 3 scaffold — buildable MV3 skeleton only. The next two issues add the
URL-filter pipeline and the real block page. No SignalR client, no Entra auth,
and no allowlist logic in here yet.

## Layout

```
extension/
├── package.json
├── tsconfig.json
├── rollup.config.mjs
├── src/
│   ├── manifest.json         — MV3 manifest (permissions, background SW)
│   ├── background.ts         — service worker entry; SignalR client lands here later
│   ├── content/
│   │   ├── block-page.html   — placeholder "blocked by Anchor" page
│   │   └── block-page.ts     — block-page script
│   └── shared/
│       └── logger.ts         — thin console wrapper, prefixed scope
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

## Dev load

1. Open `edge://extensions` in Edge.
2. Toggle **Developer mode** on (bottom-left).
3. Click **Load unpacked** and select `extension/dist/`.
4. The extension appears as **Anchor**. The background service worker should
   log `service worker started` — open it via **Inspect views: service worker**
   on the extension card to see the console.

After `npm run build` or `npm run watch` rebuilds, click **Reload** on the
extension card to pick up the new bundle.

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

## Test

No automated tests yet — the scaffold has no logic to cover. URL-filter and
block-page issues will add them.
