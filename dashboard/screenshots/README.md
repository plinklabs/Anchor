# Dashboard website screenshots

Generates the teacher-dashboard screenshots embedded on the Anchor website
(`website/`), from the real Flutter Web app rendered against deterministic demo
data. Regenerate rather than hand-edit, so the shots stay in sync with the
shipped UI.

The dashboard can't be shot live — it needs MSAL sign-in, the backend, and a
SignalR connection. So this builds a **demo web bundle** from
[`lib/main_demo.dart`](../lib/main_demo.dart): the real `AnchorDashboard` app
with **auth bypassed**, every backend call served from in-memory fakes seeded
with the stable demo data in [`lib/demo/demo_data.dart`](../lib/demo/demo_data.dart),
and a stub SignalR feed. **No backend, no real auth, no secrets.** It's the same
fake-API seam the dashboard integration tests already use (see
`integration_test/live_session_test.dart`).

## Files → website

| File | Page | Source widget |
| --- | --- | --- |
| `dashboard-home.png` | Class picker / home | `lib/pages/home_page.dart` |
| `dashboard-session.png` | Live session view | `lib/pages/session_page.dart` |
| `dashboard-bundles.png` | Bundles editor | `lib/pages/bundles_page.dart` |
| `dashboard-classes.png` | Classes / roster | `lib/pages/classes_page.dart` |
| `dashboard-history.png` | History archive | `lib/pages/history_page.dart` |
| `dashboard-past-session.png` | Past session review | `lib/pages/past_session_page.dart` |

All are 1440×900, written into `../../website/assets/`.

## Regenerating

One command, from this directory:

```bash
npm install                  # first time only — installs Playwright
node generate-screenshots.mjs
```

That builds the demo bundle (`flutter build web --target lib/main_demo.dart`),
serves it locally, drives the real app route-by-route in headless Chromium, and
writes the PNG set. To reuse an existing `../build/web-demo` build and skip the
~30 s rebuild:

```bash
node generate-screenshots.mjs --no-build
```

Output is deterministic: fixed demo data, a fixed clock baked into the data, a
fixed 1440×900 viewport, and `go_router`'s hash routes (`#/`, `#/session/…`,
`#/bundles`, …) so no server-side rewrites are needed.

## Notes

- Reuses the Playwright Chromium already installed for the extension e2e suite
  (pinned to the same `@playwright/test` version), so no extra browser download.
- The Flutter web app paints to a canvas, so the bundles shot clicks the
  catalogue row by its on-canvas position (not a DOM text locator) to open the
  editor — keep that coordinate in step with the fixed viewport if the layout
  changes.
- Demo data lives in `lib/demo/demo_data.dart`. Edit there (teacher, class `3B`,
  the roster, the allowlist, the event feed) to change what the shots show.
