# website/assets

This directory holds screenshot images for the Anchor subsite. It is populated
by the screenshot generators — do not hand-edit the images here.

## Dashboard screenshots (`dashboard-*.png`)

The teacher-dashboard shots (`dashboard-home.png`, `dashboard-session.png`,
`dashboard-bundles.png`, `dashboard-classes.png`, `dashboard-history.png`,
`dashboard-past-session.png`) are generated from the real Flutter Web dashboard
rendered against deterministic demo data — no backend, no real auth, no secrets.

Regenerate with one command:

```bash
cd ../../dashboard/screenshots
npm install            # first time only
node generate-screenshots.mjs
```

See [`dashboard/screenshots/README.md`](../../dashboard/screenshots/README.md)
for details.
