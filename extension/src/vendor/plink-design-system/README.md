# Vendored — Plink Labs design system (vanilla CSS binding)

This directory is a **verbatim copy** of the vanilla web binding from
[`plinklabs/plink-design-system`](https://github.com/plinklabs/plink-design-system),
the DS-4 / `dist/plink.css` stylesheet plus the self-hosted OFL webfonts it
references. The extension can't load the React `_ds_bundle.js`, so per
`CONSUMPTION.md` it consumes the framework-free `dist/plink.css` as a vendored
stylesheet (AF3, #164).

## Provenance

- **Source repo:** `github.com/plinklabs/plink-design-system`
- **Ref:** `develop` @ `527e2dc0e350b1a21b78d6cefcfd847c16e9f501`
  (the bindings live on `develop` until they merge to that repo's `main`)
- **Copied paths:**
  - `dist/plink.css`            → `css/plink.css`  (folder renamed — see below)
  - `assets/fonts/**`           → `assets/fonts/**`

The stylesheet itself is **byte-for-byte upstream** — only its containing folder
is renamed from `dist/` to `css/`. Upstream ships it as `dist/plink.css`, but a
folder named `dist/` here is swallowed by the extension's `dist/` `.gitignore`
rule (build output), so the file would never get committed. Renaming the folder
to `css/` keeps `assets/fonts/` as its sibling, so the stylesheet's
`@font-face src: url('../assets/fonts/…')` paths still resolve **without editing
the CSS**.

## Keeping in sync

`plink.css` is the hand-maintained vanilla mirror of the design system's
`tokens/`. When the upstream tokens/components change, **re-vendor** (re-copy
`dist/plink.css` + `assets/fonts/`) rather than hand-patching values here — a
local edit would silently drift this surface from the other Anchor surfaces.
Update the ref above when you do.

Do **not** add Anchor-specific styles to `plink.css`. Anchor's one allowed
override is the per-product accent (`--product-accent`) and its own block-page
chrome, which live in `block-page.html`, not here.
