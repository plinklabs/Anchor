# Edge Add-ons store listing assets

Images and copy uploaded to the single canonical Anchor listing on the Microsoft
Edge Add-ons dashboard, plus the generators that produce them. Regenerate rather
than hand-edit, so the listing stays in sync with the brand and the shipped UI.

## Files → dashboard fields

| File | Dashboard field | Size |
| --- | --- | --- |
| `logo-300.png` | Extension logo (required) | 300×300 |
| `screenshot-block-1280x800.png` | Screenshot — block page | 1280×800 |
| `screenshot-popup-1280x800.png` | Screenshot — status popup | 1280×800 |
| `promo-small-440x280.png` | Small promotional tile (optional) | 440×280 |
| `promo-large-1400x560.png` | Large promotional tile (optional) | 1400×560 |

## Regenerating

```bash
npm run build                              # screenshots render the built dist/
node store-listing/generate-assets.mjs       # logo + promo tiles (from design/*.svg)
node store-listing/generate-screenshots.mjs  # block page + popup, 1280×800
```

Both use the Playwright Chromium already installed for the e2e suite, so no extra
tooling is needed (ImageMagick's SVG renderer is not available/usable here).

- **Logo + tiles** are rendered from the Anchor brand mark/lockup in `../../design/`.
- **Screenshots** load the *actual* shipped `dist/` pages (block page, popup) with
  a stubbed `chrome` API and a representative demo session (class code `PLINK-3B`,
  a small sample allowlist) — so they always match what a student really sees.

## Notes

- The **logo is a placeholder** pending a redesign — it reads a bit literally. It
  can be swapped on the dashboard anytime; listing images are metadata, not part
  of the package, so changing them doesn't bump the extension version.
- The in-package toolbar icons (`../src/icons/`) are a *different* asset on a
  heavier path — changing those means a new packaged version + re-review.
