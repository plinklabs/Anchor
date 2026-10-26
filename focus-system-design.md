# Focus Session System — Design Document

A school-internal focus session system for classroom device management.

## 1. Goal

Let a teacher start a "focus session" for a class. While the session is active, each student's Windows laptop only allows the apps and websites the teacher has approved. If a student tries to leave the session or open something off-list, the teacher sees it in real time on a dashboard.

We are deliberately building **soft enforcement**: the agent actively pulls focus back and surfaces violations, but does not pretend to be tamper-proof. A motivated student can defeat it; the goal is to make off-task behavior visible, not impossible.

## 2. Scope and constraints

- **Single school, one tenant.** No multi-tenancy needed.
- **Windows only, managed or BYOD.** Most student laptops get the agent through Intune, as a Win32 app in user context (`docs/INTUNE.md`); students on their own laptop with a personal account install it themselves. Nothing depends on a laptop being managed, and nothing needs admin rights at runtime.
- **Edge is the only supported browser.** Students are signed in with their Office 365 account; Edge picks up the Entra identity automatically.
- **Teacher dashboard is web-based.** Used during lessons, sometimes while presenting.
- **One developer**, evenings and weekends, leveraging Claude Code.

## 3. Architecture overview

```
┌─────────────────────────────────────────┐
│ Student Windows laptop                  │
│                                         │
│  ┌──────────────┐    ┌───────────────┐  │
│  │ FocusAgent   │    │ Edge          │  │
│  │ (WinUI 3)    │    │ + extension   │  │
│  │              │    │               │  │
│  │ • tray       │    │ • URL filter  │  │
│  │ • overlay    │    │ • reports     │  │
│  │ • Win32 hooks│    │   active tab  │  │
│  └──────┬───────┘    └───────┬───────┘  │
│         │ SignalR            │ SignalR  │
└─────────┼────────────────────┼──────────┘
          │                    │
          ▼                    ▼
┌─────────────────────────────────────────┐
│ Azure                                   │
│                                         │
│  ┌─────────────────────────────────┐    │
│  │ App Service (ASP.NET Core)      │    │
│  │ • REST API                      │    │
│  │ • SignalR hub                   │    │
│  │ • Entra auth                    │    │
│  └─────────────┬───────────────────┘    │
│                │                        │
│  ┌─────────────▼───────────────────┐    │
│  │ Azure SQL (Standard S0)         │    │
│  │ • users, classes, sessions      │    │
│  │ • allowlists, events            │    │
│  └─────────────────────────────────┘    │
└─────────────────────────────────────────┘
          ▲
          │
┌─────────┴───────────────────────────────┐
│ Teacher dashboard                       │
│ (Flutter Web on Static Web Apps)        │
└─────────────────────────────────────────┘
```

## 4. Technology decisions

| Component | Choice | Rationale |
|---|---|---|
| Student agent | WinUI 3 + C# | Native Win32 access (P/Invoke), runs headless cleanly, looks native on Windows 11, code-signing and packaging are well-trodden in .NET. |
| Browser extension | Edge (Chromium) extension, TypeScript | Single browser to support; observes URLs and active tab from inside Edge. |
| Backend API | ASP.NET Core on Azure App Service | Pairs naturally with the C# agent, shared models, mature SignalR support. |
| Realtime channel | SignalR | Push from teacher → student agents (start/stop session), report from student → backend (foreground events, URL events). |
| Database | Azure SQL, Standard S0 (DTU) | Data is relational and EF Core works well. Started on the serverless tier for its auto-pause, but connected agents keep the database awake through the school day, so a fixed-price tier is cheaper at rollout scale (#341). |
| Event log overflow | Prune raw events > 14 days, keep summaries | Avoids growing the SQL DB unboundedly and keeps no more per-event student data than reviewing recent lessons needs (#345). Revisit later if analytics needs grow. |
| Teacher dashboard | Flutter Web on Azure Static Web Apps | Developer is already productive in Flutter; internal tool so initial-load weight is acceptable. |
| Auth | Microsoft Entra ID | Students and teachers already have school accounts; Edge and the agent can auth silently via WAM. |

### Explicitly rejected options

- **Flutter for the student agent.** All the hard work (Win32 hooks, service/UI split) would still need native code via FFI, and we lose mature .NET tooling for packaging and signing.
- **Cosmos DB.** Data is small-scale, relational, single-region.
- **Kernel-level filtering (WFP, driver).** Out of scope for soft enforcement; driver signing alone is a project.
- **Local HTTPS proxy with MITM.** Requires installing a root cert on BYOD; hard sell with parents.
- **Killing/closing off-list apps like SEB does.** Students keep their app state during sessions; we only manipulate foreground.

## 5. Student agent behaviour

### 5.1 Lifecycle

- Installs per user from a Velopack package on GitHub Releases (no admin rights, no code-signing cert): pushed by Intune through the release's install script on managed laptops, which also starts the agent right away (#457), or run by the student on their own laptop. Updates itself via Velopack deltas; Intune never pushes new versions. The one step that asks for elevation is the Edge extension policy (§6), with a single UAC prompt, and only where no Intune profile already sets that policy.
- Runs at user login as a tray app.
- Authenticates silently to the backend via Entra (WAM).
- Maintains a persistent SignalR connection.
- When idle (no active session), does nothing except heartbeat.

### 5.2 During a focus session

- Receives a `SessionStart` message via SignalR with the allowlist for this session. An agent that wasn't connected when the session started (asleep, off the network, or still connecting) asks the backend for it as soon as it connects, and carries on as if the message had arrived (#356). The extension does the same.
- Shows a brief join confirmation: *"Mr. De Vos started a focus session. Joining in 5s. [Cancel]"*. Decline is logged.
- Once joined:
  - Subscribes to `EVENT_SYSTEM_FOREGROUND` via `SetWinEventHook`.
  - On every foreground change, identifies the app (process name, executable path, signed publisher).
  - If the new foreground app is **on the allowlist**: report and do nothing.
  - If the new foreground app is **not on the allowlist**: report the event, minimize that window (`ShowWindow` with `SW_MINIMIZE`), and bring the agent's overlay (or the most recently allowed app) back to the foreground.
  - The report names the app (process name, signed publisher) and whether it was blocked. It never includes the window title or the executable path: titles carry document names, chat partners and search terms, and paths carry the Windows user name (#345).
  - A dialog that another process shows for an app's window belongs to that app (#428). Edge's upload button opens the Windows file picker in `PickerHost`, as a window owned by the Edge window: the agent follows the owner chain to the app window at its root and judges, enforces and reports the dialog as that app. An allowed app's picker stays usable; a picker opened by an off-list app, or by a paused Edge, goes down with that app (its owner window is minimized, which hides the dialog too). A picker with no owner is judged by its own process, so `PickerHost` is never allowed on its own.
- Edge is treated as one allowlisted "app" while the extension is responsible for URL-level filtering inside it.
- Without its extension Edge is an unfiltered browser, so while the extension is gone during a session the agent pauses Edge like an off-list app (#427). It knows the extension is gone because the native-messaging witness pipe between the extension and the agent drops (#146). The pipe also drops when Edge closes or the extension updates or reloads, so Edge is paused only after a short grace period (10 s) with an Edge window on screen; a grace period that ends with Edge closed starts again when Edge next comes forward. The pause lifts the moment the pipe reconnects. Only the browser's windows are minimized, so the extension can always reconnect. A switched-off extension can only be switched on again from `edge://extensions`, inside Edge, so the overlay explains the pause and offers to open Edge for a minute; if the extension doesn't come back in that time, Edge is paused again. A device where the extension never connected (not installed yet) is left alone.
- When the session ends (teacher action, or the backend ending a session still running four hours after it started, #345): unsubscribes from hooks, hides overlay, returns to idle. An agent that was offline when the session ended (asleep, or off the network) asks the backend whether its session is still on as soon as it reconnects, and returns to idle if not (#354). The extension does the same.

### 5.3 Native interop layer

All Win32 calls live in a single isolated module (`FocusAgent.Native`). This is the layer most likely to need iteration against real device behaviour. Keeping it isolated means we can fix focus-stealing edge cases in one file.

Key APIs in use:

- `SetWinEventHook` / `EVENT_SYSTEM_FOREGROUND` — foreground change events
- `GetForegroundWindow`, `GetWindowThreadProcessId` — identify the active window
- `GetAncestor` with `GA_ROOTOWNER` — find the app window that owns a dialog another process shows (#428)
- `SetForegroundWindow`, `AttachThreadInput` — pull focus back (with the documented workarounds)
- `SetWindowPos` with `HWND_TOPMOST` — keep overlay above other windows during active enforcement
- `ShowWindow` with `SW_MINIMIZE` — push off-list apps behind without closing them

### 5.4 Anti-tamper posture

We accept that BYOD without admin rights means a determined student can kill the agent. There is **no watchdog**: a student who is willing to kill the agent will simply kill the watchdog too, and the installer complexity it would add (service registration, SCM permissions, an extra StartupTask the user has to approve) buys nothing real. Instead:

- The agent registers itself to auto-start at login via a per-user `HKCU\...\Run` entry (#225), so a reboot or sign-out brings it back the next time the student signs in.
- Loss of heartbeat for > N seconds during an active session surfaces in the dashboard's live student-state panel as "agent stopped reporting" — that's the signal teachers actually act on.
- We treat tampering as a **social problem visible to the teacher**, not a technical arms race.

## 6. Edge extension behaviour

- Published once to the Edge Add-ons store; the agent gets Edge to force-install it (#211, #405). At startup the agent reads Edge's `ExtensionInstallForcelist` policy under `HKLM` and `HKCU` without elevation; if neither has Anchor's entry (an Intune/GPO-managed laptop already does), it explains the step in its own window and then adds the entry under `HKLM` through **one UAC prompt** (the system `reg.exe`, elevated), at the next free slot. The agent itself stays a per-user install, so Velopack installs and updates stay silent; only this one write is elevated. The per-user `HKCU` key isn't an option: a standard Windows profile can only read `HKCU\Software\Policies`.
- If the student says "not now", declines the prompt, or isn't an administrator, the agent falls back to a guided install (opens the store listing: *Get → Add*), stops asking at every start, and keeps offering the setup in its tray menu. Uninstalling removes the entry the agent wrote (another UAC prompt).
- With the policy in place, Edge shows "Your browser is managed by your organization". That is Edge's standard notice whenever any policy is set; here it only means Edge keeps Anchor's extension installed, and the agent's setup window says so up front, so students and parents know what they're agreeing to.
- Subscribes to `chrome.tabs.onUpdated` and `chrome.webNavigation` to observe URL changes.
- During an active session:
  - Checks each navigation against the session allowlist.
  - Blocked navigations show a friendly "this site isn't allowed during the current focus session" page and are reported to the backend.
- Communicates with the agent via SignalR (its own connection to the backend) rather than directly with the agent process. Simpler than building agent ↔ extension IPC.

## 7. Allowlist model

Teachers do not edit raw domains. They pick from **logical bundles** that the system maintains centrally.

### 7.1 Bundles

A bundle is a named group of apps and/or domains. Examples:

- **Bingel** → domains `*.bingel.be`, `*.cloudfront.net` (the specific subset Bingel uses), …
- **Smartschool** → `*.smartschool.be`, related CDN domains, …
- **Microsoft 365** → ~20 domains for Outlook, Teams, OneDrive, SharePoint, Forms
- **GeoGebra** → desktop app entry (signed publisher: International GeoGebra Institute) + `*.geogebra.org`
- **YouTube (educational only)** — a future possibility once we figure out playlist filtering

Bundles are versioned and pushed to agents/extensions as part of session start. Teachers see "Bingel"; the agent and extension see the expanded set.

### 7.2 Baseline always-allow

Every session implicitly allows:

- `*.microsoftonline.com`, `*.office.com`, `*.office365.com`, `*.microsoft.com`, `*.live.com`, `*.windows.net` — needed for Entra/Office login
- The dashboard's host(s), taken from the deployment's `Cors:AllowedOrigins` (#409) — so a teacher who runs the agent on their own laptop can still pick bundles and end the session. Students may reach it too; they can't do anything there. The backend host itself needs no entry: the extension filters only top-level navigations, not the dashboard's API calls.
- `fonts.googleapis.com`, `fonts.gstatic.com` — too common to block without breaking everything

Without this baseline, the agent's own auth and most modern web pages fail.

### 7.3 Enforcement model

Every session is strict: only bundle domains + baseline resolve; everything else is blocked. A "loose" mode (allow-everything-except-known-bad-categories) was prototyped and removed (#88) because the school firewall already covers the same ground.

### 7.4 "Site is broken" feedback loop

The extension's block page includes a "request access" button that pings the dashboard. The teacher sees pending requests during the session and can approve them for the rest of the period in one click. The request log also tells us which bundles need expanding centrally.

## 8. Joining a session

**Primary:** the teacher clicks "Start session for class 3A." The backend pushes `SessionStart` via SignalR to every signed-in agent whose user belongs to 3A. Students see the join confirmation and join automatically. No code typing.

**Fallback:** the dashboard always displays a 6-digit join code. Students can enter it in the agent's tray menu to join manually. Used for substitute teachers, students attending a different class for one period, or any case where the roster-based push doesn't apply.

**Decline is allowed.** Students can cancel during the 5-second confirmation. The teacher sees who declined; the system does not auto-force.

### Roster requirement

This needs a `Class` table and a `ClassMembership` table mapping Entra object IDs to classes. Initial population: manual import or CSV export from the school's existing system (Smartschool or equivalent). Maintenance: a class-management screen in the dashboard.

## 9. Data model (initial)

```
User                 (id, entra_oid, display_name, role: teacher|student)
Class                (id, name, school_year)
ClassMembership      (class_id, user_id, role: member|teacher)

Bundle               (id, name, version)
BundleEntry          (bundle_id, kind: domain|app, value, match_type)

Session              (id, teacher_id, class_id, mode: strict|loose,
                      started_at, ended_at, join_code)
SessionBundle        (session_id, bundle_id)
SessionParticipant   (session_id, user_id, joined_at, declined_at, left_at)

Event                (id, session_id, user_id, kind, payload_json, occurred_at)
                       kind ∈ { foreground_change, blocked_url, unblock_request,
                                heartbeat_lost, agent_killed, manual_leave }
```

Events older than 14 days are pruned once their session has ended. Aggregated counts per session per student are kept indefinitely for reporting. A session the teacher forgets to end is ended automatically four hours after it started, so its events are summarised and pruned like any other (#345).

## 10. Auth flow

- **Teacher dashboard** → MSAL.js (or `msal_flutter`) signs in against the school's Entra tenant. Token sent as bearer to the backend API.
- **Student agent** → WAM (Web Account Manager) acquires a token for the backend silently, using the Windows-signed-in account. No login screen.
- **Edge extension** → uses `chrome.identity.getAuthToken` or a redirect-based flow against Entra. Could share a session cookie via the backend if simpler in practice.

Identity is always the Entra object ID. We do not store or trust client-supplied usernames.

## 11. Build phases

### Phase 1 — backend skeleton (1 week elapsed)

- ASP.NET Core API with Entra auth.
- SQL schema and EF Core migrations.
- SignalR hub with `SessionStart` / `SessionEnd` push and `Event` ingestion.
- Stub Flutter dashboard that can sign in and start a session for a hard-coded class.

### Phase 2 — agent v1 (2–3 weeks elapsed)

- WinUI 3 tray app, Entra silent auth, SignalR client.
- Foreground hook + minimize off-list apps.
- Hand-coded allowlist (no bundles yet).
- Velopack packaging + GitHub Releases auto-update (no code-signing cert required).

### Phase 3 — extension v1 (1 week elapsed)

- Edge extension observing tab URLs, blocking with friendly page.
- Reports to backend via SignalR.
- Shares allowlist with the agent through the session payload.

### Phase 4 — bundles and dashboard polish (1–2 weeks elapsed)

- Bundle table, central editing, push to clients.
- Dashboard: class roster management, session start/stop, live event view, unblock requests.

### Phase 5 — pilot (4 weeks calendar)

- Run with 1–2 cooperative colleagues, ~30 students total.
- Iterate on the things that only show up on real hardware: focus-stealing edge cases, the Bingel/Smartschool bundle contents, presenter-mode interactions, sleep/resume behaviour.

### Phase 6 — school-wide rollout

- Installer documentation, support process, broader bundle catalogue.

Realistic calendar: **3–4 months to a state where the system can run school-wide without the developer on call every period.**

## 12. Open questions

- Do we want session schedules tied to the school timetable (auto-start at the bell), or always teacher-initiated?
- How do we handle a student who joins a class mid-year — manual roster edit, or sync from the school system on a schedule?
- Should the agent also enforce during exams (stricter mode, blocks even the cancel button)? If yes, this is closer to SEB territory and probably deserves a separate mode.
- Reporting: do teachers want per-student summaries after each session, or just live data?

## 13. Non-goals

- Tamper resistance against motivated, technically skilled students.
- Filtering anything outside Edge at the URL level.
- Working on Mac, Linux, ChromeOS, iPads, phones.
- Replacing SEB for high-stakes exams.
- Parental controls outside of school sessions.
