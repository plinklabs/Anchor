# FocusAgent (student agent)

Windows-only WinUI 3 desktop app that runs on each student's laptop during a
focus session.

Design rationale lives in [focus-system-design.md](../focus-system-design.md)
§5 (student agent behaviour) and §5.3 (native interop layer).

## Layout

```
agent/
├── FocusAgent.sln
├── src/
│   ├── FocusAgent.App      — WinUI 3 desktop app, tray + hidden main window. Builds unpackaged; ships via Velopack.
│   ├── FocusAgent.Core     — DTOs (mirroring backend SignalR payloads), settings, log paths
│   └── FocusAgent.Native   — Win32 P/Invoke surface (foreground watcher, focus enforcer, app identifier)
└── tests/
    ├── FocusAgent.Core.Tests   — xUnit tests for Core
    └── FocusAgent.Native.Tests — xUnit tests for Native
```

The Native project is intentionally isolated so all Win32 interop has a single
boundary to audit and iterate against real device behaviour.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Windows 10 1809 (build 17763) or newer for runtime; build host should be Windows 11
- Visual Studio 2022 17.10+ is convenient for XAML designer/debugging but not required

The first `dotnet restore` pulls Windows App SDK 1.7 and H.NotifyIcon.WinUI as
NuGet packages — no `dotnet workload` install is required for unpackaged WinUI 3
apps on .NET 10.

## Build

```powershell
cd agent
dotnet restore
dotnet build -c Debug -p:Platform=x64
```

## Versioning

The agent has a **single version source**: `<VersionPrefix>` in
[`agent/Directory.Build.props`](Directory.Build.props) (#208). MSBuild
auto-imports it into every agent project, so that one number drives:

- the built `.dll`/`.exe` `AssemblyVersion` / `FileVersion`,
- the `InformationalVersion` the running agent reports on its `/status`
  endpoint (so the shipped binary self-reports its version), and
- the Velopack package version at pack time (the pack step reads the same
  property).

A unit test (`VersionSourceTests`) fails CI if that version doesn't flow into the
built assemblies. The design-system submodule under `external/` has its own
`Directory.Build.props` and versions independently — by design.

**Release convention:** bump `<VersionPrefix>` in `Directory.Build.props`,
commit, then push a `agent-v<version>` tag — the
tag is what triggers the Velopack publish workflow. The extension versions
independently from its own single source (`extension/package.json`); see
[`extension/README.md`](../extension/README.md).

## Run

```powershell
cd agent
dotnet run --project src/FocusAgent.App -c Debug -p:Platform=x64
```

The app starts hidden and places a tray icon. Right-click it for **Open**
(brings up the main window) and **Quit**. Only one instance can run at a time;
launching a second copy exits immediately.

`dotnet run` stays attached to the running process. Ctrl+C in the terminal
does **not** stop the app — WinUI 3 apps are WinExe with no console attached,
so the signal never reaches them. Use the tray Quit, or
`Stop-Process -Name FocusAgent.App` from another shell.

## Test

```powershell
cd agent
dotnet test
```

This runs the fast unit/native suites (`FocusAgent.Core.Tests`,
`FocusAgent.Native.Tests`). The headless end-to-end suite is separate — see
below.

## End-to-end (headless) verification

`tests/FocusAgent.IntegrationTests` is an asserting integration suite that boots
the **real backend** and launches the **real agent exe** headless, then drives
session lifecycle over REST (as the dashboard would) and asserts against the
agent's `/status` endpoint and the backend's event feed. It's the agent-side
analog of the extension's Playwright harness (`extension/e2e`, #124/#130) and
replaces the manual `scripts/dev/verify-*.ps1` smokes for the state paths.

The harness needs no WAM picker and no second machine: the agent runs with
`--inject-token` (bypasses WAM, authenticates via `X-Dev-Impersonate-Oid`),
`--status-endpoint <port>` (JSON state on loopback), and — for participant
flows — `--auto-join`. Under `--inject-token` the agent also layers environment
variables over its config, so each launch is pointed at a throwaway test
backend (`Backend__BaseUrl`) and impersonates the seeded student it needs
(`Dev__ImpersonateOid`) without rewriting any file.

Covered flows (Phase 1 — state paths): session start, mid-session bundle switch,
session end, join-by-code (incl. 404/429 error paths), and heartbeat liveness
(the agent keeps the session alive; killing it makes the backend record
`HeartbeatLost`).

```powershell
# Build the agent exe the suite launches (x64 Debug), then run the suite.
dotnet build agent/src/FocusAgent.App/FocusAgent.App.csproj -p:Platform=x64 -c Debug
dotnet test agent/tests/FocusAgent.IntegrationTests/FocusAgent.IntegrationTests.csproj
```

The suite boots its own backend on a dedicated port (5282) against a throwaway
SQLite DB under the temp dir, so it never touches a running dev backend or
`anchor.dev.db`. It runs in CI on a Windows runner via
[`.github/workflows/agent-e2e.yml`](../.github/workflows/agent-e2e.yml).

### Visual enforcement (Phase 2, #133)

The agent's WinUI surfaces are pure DirectComposition — there's no `/status`
field to poll — so they're asserted by **screenshot capture** instead: the spec
drives the agent's self-test flags (synthetic payloads, no backend), finds the
surface's HWND, and BitBlts its rect with `CAPTUREBLT` while the process is
per-monitor DPI-aware (see `WindowCapture.cs`; same path as the matching
`scripts/dev/verify-*.ps1`). The self-tests are:

| Flag | Surface | Spec / verify script |
|---|---|---|
| `--show-test-overlay` | focus-enforcement overlay (#33) | `OverlayVisualTests` / `verify-overlay.ps1` |
| `--show-test-toast` | join-confirmation toast (#41) | `ToastVisualTests` / `verify-toast.ps1` |
| `--show-test-mainwindow` | redesigned MainWindow (#173) | `MainWindowVisualTests` |
| `--show-test-joinbycode` | redesigned join-by-code dialog (#175) | `JoinByCodeVisualTests` |
| `--show-test-traymenu` | redesigned tray context menu (#176) | `TrayMenuVisualTests` / `verify-traymenu.ps1` |
| `--show-test-guided-install` | guided-install fallback window (#211) | `GuidedInstallVisualTests` / `verify-guided-install.ps1` |

The redesign specs assert the real ink surface paints (not blank), is
dark-dominated (the DS ink treatment, not the desktop or an OS grey popup), and
carries the one magenta spark. The tray menu is a `MenuFlyout` (a popup, not a
window) that a headless run can't open by clicking the tray — so its self-test
builds the very same menu via the shared `TrayMenu` factory and shows it open
over a small ink host window, the rect the spec captures. The overlay spec also
asserts its close path tears the window down. Captured PNGs are written under
`TestResults/visual-artifacts/` for eyeball triage.

These specs carry the `Category=Visual` trait and are **not** in the state
collection (they need no backend). Run them on their own:

```powershell
dotnet test agent/tests/FocusAgent.IntegrationTests/FocusAgent.IntegrationTests.csproj --filter "Category=Visual"
```

Because they render real DirectComposition surfaces, they're flakier on a
headless runner than the JSON-state suite. In CI they run as a separate,
**non-blocking** (`continue-on-error`) step while the flake rate is characterized
over several runs; the blocking state run uses `--filter "Category!=Visual"`. The
state suite stays the high-value, low-flake gate.

#### Curated website screenshots (#251)

The same self-test surfaces double as the source for the student-facing
screenshots on the [Anchor website](../website). A separate opt-in generator,
`WebsiteScreenshots`, drives the join toast, the app-block overlay, the main
window, and the tray menu through the identical launch + capture path, but
against presentable demo content (teacher "Ms Rivera", class `PLINK-3B`, a
readable allowlist — see `FocusAgent.App.SelfTestDemoContent`, matched to the
dashboard's demo data) and writes a fixed, named PNG set straight into
`website/assets/` instead of the timestamped `TestResults/` triage dump.

It carries the `Category=WebsiteScreenshots` trait (so a `Category=Visual` run
never touches it) and is gated on `ANCHOR_WEBSITE_SHOTS=1` (so an unfiltered run
never overwrites the committed images). Regenerate with one command:

```powershell
./scripts/dev/generate-website-screenshots.ps1
```

That builds the agent, sets the gate, runs the generator, and writes
`agent-join-toast.png`, `agent-block-overlay.png`, `agent-main-window.png`, and
`agent-tray-menu.png` into `website/assets/`. The browser-side shots (the
extension block page + popup) are reused from `extension/store-listing/` and are
not regenerated here. See [`website/assets/README.md`](../website/assets/README.md).

**Still deferred:** the #92 off-list-window re-minimize-on-restore path. Unlike
the overlay/toast it has no self-test seam — exercising it end-to-end needs a
real off-list window foregrounded and a real `EVENT_SYSTEM_FOREGROUND` hook fire,
which is high-flake on a headless desktop. Its enforcement logic is covered by
`FocusSessionControllerTests` (`Blocked_app_is_reminimized_on_every_reactivation_within_window`);
a real-window e2e is tracked as a follow-up.

### One-command dev loop

`scripts/dev/dev-agent.ps1` is the agent analog of `npm run dev:extension`: it
boots a seeded backend (reusing one already running), launches the real agent
headless impersonating the seeded Dev Student, and opens a small REST console to
start / amend / end a session and print the agent's live `/status`.

```powershell
./scripts/dev/dev-agent.ps1
```

## Localization (i18n)

The agent is localized (#323, part of the cross-surface effort #320) with the
standard WinUI/.NET resource pipeline: `.resw` catalogues under
[`src/FocusAgent.App/Strings/<lang>/Resources.resw`](src/FocusAgent.App/Strings),
compiled into `resources.pri` next to the exe by the Windows App SDK build.

- **English (`en-US`) is the source + fallback locale** — set via
  `<DefaultLanguage>` in the csproj. A key missing from the active language (or an
  unsupported display language) resolves to the English value, never a blank or a
  raw key. `nl-NL` (Dutch) ships as the proof-of-concept locale.
- **Locale resolution** is the user's Windows display language, picked
  automatically by MRT. The `--ui-language <bcp47>` dev flag forces a specific
  language (e.g. `--ui-language nl-NL`) so Dutch can be exercised on an
  English box without changing the machine's display language.
- **How strings resolve.** Every user-facing string goes through the
  [`Loc`](src/FocusAgent.App/Localization/Loc.cs) helper — static window copy is
  set from each window's constructor, dynamic copy (tray, connection status,
  toasts, join errors) at the point it's built. `Loc` is the only seam that
  touches `ResourceLoader`/`ResourceManager`.

  > **Why not `x:Uid`?** The agent ships **unpackaged** (via Velopack), where
  > `ApplicationLanguages.PrimaryLanguageOverride` throws — so the framework's own
  > `x:Uid` resolution can only ever follow the machine's language list and can't
  > be pointed at a chosen language. Applying strings in code against an explicit
  > MRT `ResourceContext` gives one uniform path that works both by OS language and
  > forced. Diagnostic strings that embed HTTP codes, URLs, or MSAL/WAM error codes
  > (the connection-failure detail lines, `AuthFailureMessage`) and the last-resort
  > crash dialog stay English by design — the issue's back-end/log-string allowance.

### Adding a new locale

1. Copy [`Strings/en-US/Resources.resw`](src/FocusAgent.App/Strings/en-US/Resources.resw)
   to `Strings/<lang>/Resources.resw` (e.g. `de-DE`, `fr-FR`) and translate each
   `<value>`. Keep the `name` keys identical to `en-US` and reuse the same
   `{0}`/`{1}` placeholders — leave a key out only if there is genuinely nothing to
   translate (it then falls back to English).
2. `dotnet test` — [`I18nCatalogueParityTests`](tests/FocusAgent.Core.Tests/I18nCatalogueParityTests.cs)
   checks the new catalogue carries exactly the `en-US` keys (and matching
   placeholders), so a missing/stray key or a dropped placeholder fails fast.
3. Verify end-to-end against the real exe: the agent's `--verify-i18n <lang>` mode
   resolves a representative set of strings for that language and writes them to
   the file named by `ANCHOR_I18N_RESULT_PATH`;
   [`I18nTests`](tests/FocusAgent.IntegrationTests/I18nTests.cs) drives it for
   `en-US`, `nl-NL`, and an unsupported language to prove translation + fallback.
   Or just eyeball a surface: launch any self-test with `--ui-language <lang>`
   (e.g. `--show-test-traymenu --ui-language nl-NL`).

## Configuration

`src/FocusAgent.App/appsettings.json` carries:

| Key | Default | Notes |
| --- | --- | --- |
| `Backend:BaseUrl` | `http://localhost:5276` | Dev backend URL. Substituted per deploy via `appsettings.Production.json` — see [Per-deployment config](#per-deployment-config-release-builds). |
| `Backend:HubPath` | `/hubs/session` | Path of the backend SignalR hub. |
| `Auth:TenantId` | _empty_ | Entra tenant ID the agent signs in against. |
| `Auth:ClientId` | _empty_ | App registration (public client) ID. |
| `Auth:Scope` | `<backend-client-id>/.default` | Backend API scope requested for the access token. Uses the bare-GUID form (no `api://` prefix), matching the dashboard — Entra rejects `api://`-form requests when the agent and API share a tenant via `AADSTS90009`. Backend accepts both audience forms. |
| `Auth:LoginHint` | _empty_ | Optional UPN used as a hint when WAM has to prompt interactively. Useful on machines whose Windows account is not on the school tenant (e.g. dev laptops) — set it to the school UPN to pre-fill the WAM picker. Has no effect once a school-tenant account is cached. |
| `Dev:ImpersonateOid` | _empty_ | **Dev-only.** GUID of a seeded user OID to impersonate on the hub connection. Sends `X-Dev-Impersonate-Oid` on the SignalR negotiate request; the backend honors it only when running with `ASPNETCORE_ENVIRONMENT=Development`. See [Single-machine dev verification](#single-machine-dev-verification) below. |

`Auth:TenantId`, `Auth:ClientId` and `Auth:Scope` are required. The agent fails
fast at startup with a clear error message if any are empty.

### Per-deployment config (release builds)

`Backend:BaseUrl` and `Auth` are **substitutable per deployment** so a fork's
published agent targets its own backend + Entra without editing the committed
dev defaults (#203). The agent loads `appsettings.{Environment}.json` *after*
`appsettings.json`, where the environment comes from `DOTNET_ENVIRONMENT` /
`ASPNETCORE_ENVIRONMENT`, defaulting to the build configuration: **Debug ⇒
Development**, **Release ⇒ Production** (an explicit env var always wins). So:

- `dotnet run` and the headless e2e (Debug, no env var) stay on **Development**
  and load the local `appsettings.Development.json` against the dev backend —
  unchanged.
- A **release build** runs in **Production** and loads
  `appsettings.Production.json`. That file is committed as a *template* whose
  `Backend:BaseUrl` + `Auth` values are `#{…}#` placeholders; the release /
  Velopack pack step substitutes them so the published agent points at the
  fork's backend. Any key it omits falls back to `appsettings.json`.

To smoke-test the Production layer locally, fill the placeholders in a copy of
`appsettings.Production.json` next to the built exe and launch with
`DOTNET_ENVIRONMENT=Production`.

#### Backend URL handed to the extension (#204)

The browser extension is **backend-agnostic**: one published Edge listing serves
every fork, so it learns which backend to target from the on-box agent at
runtime rather than baking the URL in. The agent's native-messaging witness host
(`anchor-witness-host.exe`) hands the URL to the extension over the existing
witness channel as soon as the link opens — and only the registered host can
reach the extension's port, so an arbitrary web page can't repoint it.

The host resolves which URL to send, in order:

1. `ANCHOR_WITNESS_BACKEND_URL` env var — the per-deployment source the agent
   installer sets (and the extension e2e harness sets to point at its throwaway
   backend).
2. a `backend-url.json` file (`{"backendUrl":"https://…"}`) next to the host exe.
3. the dev fallback `http://localhost:5276` so a plain dev loop works before any
   deployment config exists.

A production installer should set `ANCHOR_WITNESS_BACKEND_URL` (or drop the file)
to the same backend the agent's `Backend:BaseUrl` targets.

### Single-machine dev verification

Verifying student-agent behaviour (`SessionStarted`, the join-confirmation
toast, decline, focus enforcement) historically required a second machine
signed in with a second school Entra account. To unblock single-machine
verification ([issue #38](https://github.com/yvanvds/Anchor/issues/38)),
the backend accepts a dev-only impersonation header on the hub:

Set `Dev:ImpersonateOid` in `appsettings.Development.json` to the OID of a
seeded user (e.g. the dev `Dev Student` at
`22222222-2222-2222-2222-222222222222`):

```json
{
  "Dev": {
    "ImpersonateOid": "22222222-2222-2222-2222-222222222222"
  }
}
```

The agent sends `X-Dev-Impersonate-Oid: <oid>` on the SignalR negotiate
request, and the backend resolves the hub's current user from that OID
instead of the token's `oid` claim. With this set you can:

- Run the dashboard signed in as your real teacher account and run the
  agent on the same machine acting as a seeded student — start a session
  from the dashboard, the agent receives `SessionStarted` and exercises
  the full join-confirmation / decline / focus flow under the seeded
  student identity.
- Switch identity by changing the OID — useful for testing "two students"
  scenarios from one laptop by relaunching the agent with a different
  seeded OID.

The override is honored **only** when the backend is running with
`ASPNETCORE_ENVIRONMENT=Development`; production rejects the header.

### Local overrides

`appsettings.Development.json` (gitignored, sits next to `appsettings.json`) is
loaded after the base config so it can override individual keys without
touching the committed defaults. Use it on dev machines to supply the agent's
tenant/client IDs (and optionally a `LoginHint`) without committing them:

```json
{
  "Auth": {
    "TenantId": "<school-tenant-guid>",
    "ClientId": "<agent-public-client-guid>",
    "LoginHint": "you@school.example"
  }
}
```

The file is wired into the build with `<CopyToOutputDirectory>PreserveNewest`,
so creating it in `src/FocusAgent.App/` is enough — no extra MSBuild glue
needed.

Logs roll daily into `%LOCALAPPDATA%\Anchor\FocusAgent\logs\focusagent-*.log`
(14-day retention) via Serilog, plus debug output during development.

## Packaging & distribution

The agent ships **unpackaged**, distributed via **Velopack** on GitHub Releases
(see [`docs/RELEASE.md`](../docs/RELEASE.md)). There is no MSIX/Store path —
Anchor is BYOD and is never pushed to devices via Intune/MDM. Startup at login is
handled by a per-user `HKCU\...\Run` entry written by the Velopack install hook,
not a packaged startup task (see `FocusAgent.Core.Startup.StartupRegistrar`,
#225).
