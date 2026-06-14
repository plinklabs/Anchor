# Anchor icon generation

All shipped Anchor icons, tiles, splash screens and favicons are **derived from
the one mark** in [`../anchor-mark.svg`](../anchor-mark.svg) — there is no
hand-edited binary. [`generate.mjs`](generate.mjs) composes the mark onto the
correct per-surface background (see [`../ANCHOR_BRAND.md`](../ANCHOR_BRAND.md))
and rasterises it to every target path.

## Regenerate

```sh
cd design/icons
npm install
node generate.mjs
```

The committed outputs are what ship; this folder is the recipe, not a build
step. `node_modules/` is not committed. Re-run after any change to the mark or
the brand colours, then commit the regenerated assets.

## What it produces

| Surface | Treatment | Targets |
|---|---|---|
| Dashboard (teacher, **paper/light**) | indigo `#34357A` mark | `dashboard/web/favicon.png`, `dashboard/web/icons/Icon-{192,512}.png`, maskable on a paper plate |
| Agent (student, **ink/dark**) | indigo `#7E80D2` on ink `#1B1B23` | `agent/.../Assets/Square{44,150,310}*Logo.png`, `Wide310x150Logo.png`, `StoreLogo.png`, `SplashScreen.png` |
| Agent tray | indigo `#7E80D2`, **transparent** (floats on the taskbar) | `agent/.../Assets/TrayIcon.ico` |
| Extension (student, **ink/dark**) | indigo `#7E80D2`, transparent | `extension/src/icons/icon-{16,32,48,128}.png` |
