# Focus Session System — Design Document

A school-internal focus session system for classroom device management.

## 1. Goal

Let a teacher start a "focus session" for a class. While the session is active, each student's Windows laptop only allows the apps and websites the teacher has approved. If a student tries to leave the session or open something off-list, the teacher sees it in real time on a dashboard.

We are deliberately building **soft enforcement**: the agent actively pulls focus back and surfaces violations, but does not pretend to be tamper-proof. A motivated student can defeat it; the goal is to make off-task behavior visible, not impossible.

## 2. Scope and constraints

- **Single school, one tenant.** No multi-tenancy needed.
- **BYOD, Windows only.** No MDM, no admin rights at runtime.
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
│  │ Azure SQL (Serverless)          │    │
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
| Database | Azure SQL, Serverless tier | Data is relational; serverless tier auto-pauses outside school hours; cheap and EF Core works well. |
| Event log overflow | Prune raw events > 30 days, keep summaries | Avoids growing the SQL DB unboundedly. Revisit later if analytics needs grow. |
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

- Installs once per laptop (MSIX installer, code-signed).
- Runs at user login as a tray app.
- Authenticates silently to the backend via Entra (WAM).
- Maintains a persistent SignalR connection.
- When idle (no active session), does nothing except heartbeat.

### 5.2 During a focus session

- Receives a `SessionStart` message via SignalR with the allowlist for this session.
- Shows a brief join confirmation: *"Mr. De Vos started a focus session. Joining in 5s. [Cancel]"*. Decline is logged.
- Once joined:
  - Subscribes to `EVENT_SYSTEM_FOREGROUND` via `SetWinEventHook`.
  - On every foreground change, identifies the app (process name, executable path, signed publisher).
  - If the new foreground app is **on the allowlist**: report and do nothing.
  - If the new foreground app is **not on the allowlist**: report the event, minimize that window (`ShowWindow` with `SW_MINIMIZE`), and bring the agent's overlay (or the most recently allowed app) back to the foreground.
- Edge is treated as one allowlisted "app" while the extension is responsible for URL-level filtering inside it.
- When the session ends (teacher action, timer expiry, or class period end): unsubscribes from hooks, hides overlay, returns to idle.

### 5.3 Native interop layer

All Win32 calls live in a single isolated module (`FocusAgent.Native`). This is the layer most likely to need iteration against real device behaviour. Keeping it isolated means we can fix focus-stealing edge cases in one file.

Key APIs in use:

- `SetWinEventHook` / `EVENT_SYSTEM_FOREGROUND` — foreground change events
- `GetForegroundWindow`, `GetWindowThreadProcessId` — identify the active window
- `SetForegroundWindow`, `AttachThreadInput` — pull focus back (with the documented workarounds)
- `SetWindowPos` with `HWND_TOPMOST` — keep overlay above other windows during active enforcement
- `ShowWindow` with `SW_MINIMIZE` — push off-list apps behind without closing them

### 5.4 Anti-tamper posture

We accept that BYOD without admin rights means a determined student can kill the agent. There is **no watchdog**: a student who is willing to kill the agent will simply kill the watchdog too, and the installer complexity it would add (service registration, SCM permissions, an extra StartupTask the user has to approve) buys nothing real. Instead:

- The agent registers itself to auto-start at login via the MSIX `windows.startupTask` extension, so a reboot or sign-out brings it back the next time the student signs in.
- Loss of heartbeat for > N seconds during an active session surfaces in the dashboard's live student-state panel as "agent stopped reporting" — that's the signal teachers actually act on.
- We treat tampering as a **social problem visible to the teacher**, not a technical arms race.

## 6. Edge extension behaviour

- Installs alongside the agent (side-loaded via registry for now; could move to Edge Add-ons private listing later).
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
- Our own backend domain
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

Events older than 30 days are pruned. Aggregated counts per session per student are kept indefinitely for reporting.

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
- MSIX packaging, code-signing cert acquisition (start procurement early — multi-week lead time).

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
