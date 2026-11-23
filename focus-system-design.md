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
│  │ FocusAgent   │ NM │ Edge          │  │
│  │ (WinUI 3)    │◄──►│ + extension   │  │
│  │              │    │               │  │
│  │ • tray       │    │ • URL filter  │  │
│  │ • overlay    │    │ • block page  │  │
│  │ • Win32 hooks│    │               │  │
│  └──────┬───────┘    └───────────────┘  │
│         │ SignalR                       │
└─────────┼───────────────────────────────┘
          │
          ▼
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

NM is native messaging: the agent and the extension talk over a local link, and
the agent is the only one with a backend connection. It hands the extension the
session it joined and reports what the extension sees (#489; §6, §10).

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

- Installs per user from a Velopack package on GitHub Releases (no admin rights, no code-signing cert): pushed by Intune on managed laptops as a Win32 app that runs the release's `Setup.exe` silently and then starts the agent, or run by the student on their own laptop. Updates itself via Velopack deltas. Intune only pushes a new version when its detection rule's minimum version is raised on purpose, which restarts an older running agent (see [docs/INTUNE.md](docs/INTUNE.md)). The one step that asks for elevation is the Edge extension policy (§6), with a single UAC prompt, and only where no Intune profile already sets that policy.
- Runs at user login as a tray app.
- Authenticates silently to the backend via Entra (WAM).
- Maintains a persistent SignalR connection.
- When idle (no active session), does nothing except heartbeat.

### 5.2 During a focus session

- Receives a `SessionStart` message via SignalR with the allowlist for this session. An agent that wasn't connected when the session started (asleep, off the network, or still connecting) asks the backend for it as soon as it connects, and carries on as if the message had arrived (#356). The extension does the same.
- Shows a brief join confirmation: *"Mr. De Vos started a focus session. Joining in 5s. [Cancel]"*. Decline is logged.
- Once joined:
  - Subscribes to `EVENT_SYSTEM_FOREGROUND` via `SetWinEventHook`.
  - Minimizes the off-list windows already open (#104). The one with the foreground goes last, and minimizes itself, so Windows hands the foreground, and the keyboard, to the window the student then sees in front: an allowed app, or explorer's desktop or taskbar when none is left (#560). Minimized by the agent like the others, it stayed the foreground window and kept getting what the student typed. No overlay comes up for it.
  - On every foreground change, identifies the app (process name, executable path, signed publisher).
  - If the new foreground app is **on the allowlist**: report and do nothing.
  - If the new foreground app is **not on the allowlist**: report the event, minimize that window (`ShowWindow` with `SW_MINIMIZE`), and bring the agent's overlay (or the most recently allowed app) back to the foreground.
  - The report names the app (process name, signed publisher) and whether it was blocked. It never includes the window title or the executable path: titles carry document names, chat partners and search terms, and paths carry the Windows user name (#345).
  - A dialog that another process shows for an app's window belongs to that app (#428). Edge's upload button opens the Windows file picker in `PickerHost`, as a window owned by the Edge window: the agent follows the owner chain to the app window at its root and judges, enforces and reports the dialog as that app. An allowed app's picker stays usable; a picker opened by an off-list app, or by a paused Edge, goes down with that app (its owner window is minimized, which hides the dialog too). A picker with no owner is judged by its own process, so `PickerHost` is never allowed on its own.
- Edge is treated as one allowlisted "app" while the extension is responsible for URL-level filtering inside it.
- Without its extension Edge is an unfiltered browser, so while the extension is gone during a session the agent pauses Edge like an off-list app (#427). It knows the extension is gone because the native-messaging witness pipe between the extension and the agent drops (#146). Each Edge profile has its own copy of the extension and its own pipe connection, and the agent serves them all at once, so a second profile gets the session too (#517); the extension counts as gone when the last connection drops. The pipe also drops when Edge closes or the extension updates or reloads, so Edge is paused only after a short grace period (10 s) with an Edge window on screen; a grace period that ends with Edge closed starts again when Edge next comes forward. The teacher is told at that same moment, once per outage ("…Anchor extension was switched off or removed", `extension_disabled`), and never before: an extension that restarts (an option changed on its page, an update, a reload) and an Edge that is closed tell the teacher nothing (#518). With several profiles the pipe can't show the extension being switched off in one of them while another's is still connected, and a connection that drops is as often a profile being closed. The agent reads that from Edge itself: each browser window carries the ID of its profile, and each profile's preferences say whether the extension is on. A profile that is open with the extension switched off or removed pauses Edge and is reported the same way; a closed profile is neither. The pause lifts the moment the pipe reconnects (or that profile has the extension again, or is closed). Only the browser's windows are minimized, so the extension can always reconnect. A switched-off extension can only be switched on again from `edge://extensions`, inside Edge, so the overlay explains the pause and offers to open Edge for a minute; if the extension doesn't come back in that time, Edge is paused again. A device where the extension never connected and isn't installed in any Edge profile is left alone. Installed but never connected since the agent started is not (#556): the session never reached the extension, so Edge filtered nothing. The agent registers the host again at the join, waits the grace period, then opens Edge in the open profile that has the extension at a page the session allows, which wakes the extension, and waits once more; if it still doesn't connect, Edge is paused and the teacher told that the extension isn't connected (`extension_not_connected`), not that it was switched off. Edge open only in profiles that never had the extension has nothing to wake and nothing switched off (#519): those profiles are outside the school profile, so with one, the school-profile rule below pauses their windows one by one; without one (or with one that lacks the extension), Edge is paused for that, as with the link up, and once there is one the link is waited for again.
- During a session only the Edge profile signed in with the student's school account may be used (#525). Serving every profile right kept leaving gaps (#517, #518, #519), and Guest and InPrivate windows are further ways around the extension; permanent Edge policies were rejected because they would also restrict the laptop outside lessons and don't reach laptops outside Intune. The agent reads which account each profile is signed in with from Edge's `Local State` (`profile.info_cache`): the school profile is the one whose `edge_account_oid` is the agent's own account's object ID, or, for a profile without one, whose address matches, ignoring case; it must also have the extension. Every couple of seconds it compares Edge's open windows against it: each browser window carries its profile's AppUserModelID, a Guest window the `Guest Profile`'s, and an InPrivate window its own profile's with `[InPrivate]` in its title (measured on Edge 157). A window of another profile, a Guest window and an InPrivate window are paused on their own, through the same overlay and hand-over as the Edge pause, while the school profile's windows keep working. A site installed as an app (#537) has windows of its own, which Edge 157 gives the AppUserModelID of a package it registers for the app (`<package family>!App`) in every profile: the package's manifest names the app, and `Local State` lists the profiles it is installed in (`app_shims`); an app window of another profile is paused like that profile's browser windows, and the school profile's own (Teams or Outlook installed as an app) keep working. An app installed in the school profile and another has Edge end each window's title with its profile's name, which the agent matches against the name the school profile's browser windows show. The overlay says "During the lesson you work in your school profile", and its button closes that window and brings the school profile forward (or opens Edge in it). With no profile signed in with the school account, Edge is paused as a whole, with a message that names the account ("You need to sign in to Edge with your school account: …") and a button that opens Edge for a minute so the student can sign in (Edge doesn't open its own settings pages from outside); the pause lifts once such a profile has the extension. When a profile is signed in with the school account but doesn't have the extension (#536; a laptop with the guided install, where the student signed in to another profile), the pause says Anchor still has to be added to the school profile instead, and its button opens the store listing in that profile (`msedge --profile-directory="<folder>" <store url>`, the guided install's listing). The teacher is told only what the student leaves unresolved for a minute: a window outside the school profile still open (`outside_school_profile`; an InPrivate window still open is reported by the agent's InPrivate witness as `inprivate_opened`, and only then since #535, not the moment it opens), still no profile signed in with the school account (`school_profile_missing`), or still no extension in the one that is (`school_profile_without_extension`). Signing in and adding the extension each get that minute of their own. Nothing is restricted outside a session, on a device where the extension never connected and isn't installed in any Edge profile, or while Edge's `Local State` can't be read. Installed but never connected is guarded like the Edge pause (#519): otherwise a profile that never had the extension browsed unfiltered next to the one that has it, and nobody heard of it. Other tools that read Edge from outside, such as the address-bar check (#526), look only at windows of the school profile (`SchoolProfileGuard.IsSchoolProfileWindow`).
- Test phase only (#526, on #515's list): during a session the agent also reads the address bar of the school profile's foreground Edge window through UI Automation, a second witness of what the extension lets through. The extension is the only thing that sees inside Edge, so a gap in it was a silent one. The agent holds the page's host against the allowlist the extension was handed, matched as the extension matches it, and an off-list host the address bar keeps showing past a short grace (the extension redirects an off-list navigation as it starts) is reported once per session as `UnfilteredSite`, with the host only; the teacher's live feed and the past session say so, without a tamper flag or an alert, since the gap may be the extension's. It reports and nothing more: no pause, because UI Automation on the address bar can break with an Edge update. Nothing is read outside a session or in other windows, nothing is kept, logged or sent for an allowed site, and the block page, Edge's own pages, the new-tab page, local files and what the student is typing are never reported. After the pilot it goes, or stays without the site (#515).
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
  - Reads its own settings on every session the agent hands down and on every permission change, and tells the agent (#524). While its "Site access" isn't "On all sites", the agent pauses Edge with a button that has the extension open its own page; the teacher is told only if the student doesn't put it back.
  - Blocked navigations show a friendly "this site isn't allowed during the current focus session" page and are reported to the backend through the agent. The page shows the student the whole URL; the report names its host only, never the URL, whose path and query string can hold search terms and other personal detail (#527).
- Talks only to the agent, over native messaging (a small host the agent registers, which bridges to the agent's local pipe). The agent hands it the session once the student has joined it (so a declined session never reaches the extension), every allowlist change, and the end of the session, and on every (re)connect the current session again. The extension hands back blocked pages (by host) and unblock requests, which the agent reports with its own connection (#489), and what its own settings allow (#524). It reports no tampering of its own: InPrivate windows are the agent's to report (#148, #535), and the extension stopped sending its own InPrivate report in #539. The extension has no backend connection and no sign-in, so nothing a student does to the browser's account can leave Edge unfiltered.
- The same link is the agent's witness of the extension (#146): when it drops during a session and stays down past the grace period with Edge open, the agent pauses Edge until the extension is back (#427) and reports `extension_disabled` (#518; see §5 for the cases that report nothing). With the session coming from the agent, the cases are: extension connected (it filters), or not connected (Edge paused). The exception is a device where the extension never connected and isn't installed in any profile, which the pause leaves alone; installed but never connected is woken, then paused and reported as `extension_not_connected` (#556).

## 7. Allowlist model

Teachers do not edit raw domains. They pick from **logical bundles** that the system maintains centrally.

### 7.1 Bundles

A bundle is a named group of apps and/or domains. Examples:

- **Bingel** → domains `*.bingel.be`, `*.cloudfront.net` (the specific subset Bingel uses), …
- **Smartschool** → `*.smartschool.be`, related CDN domains, …
- **Microsoft 365** → domains for Outlook, Teams, OneDrive, SharePoint, Forms. That includes `*.cloud.microsoft` and `*.microsoft365.com` (#531): Microsoft 365 on the web lives on `*.cloud.microsoft` now (www.office.com and www.microsoft365.com redirect to the home page, m365.cloud.microsoft; portal.office.com and sign-in pass through www.microsoft365.com; Outlook, Teams, Word, Forms and the rest have `*.cloud.microsoft` hosts). It also lists the shortcuts a student types, `outlook.com` and `onedrive.com` and their `www.` hosts (#532), as exact entries: they redirect on to Outlook and onedrive.live.com, but the extension judges a navigation by its first URL, so the shortcut itself has to be on the list. Exact, because `*.outlook.com` would also open the rest of consumer mail and the Safe Links hosts. A redirect's target is judged as well, once it commits.
- **GeoGebra** → desktop app entry (signed publisher: International GeoGebra Institute) + `*.geogebra.org`
- **YouTube (educational only)** — a future possibility once we figure out playlist filtering

Bundles are versioned and pushed to agents/extensions as part of session start. Teachers see "Bingel"; the agent and extension see the expanded set.

The example bundles are seeded (`DevDataSeeder` in development, `scripts/seed-bundles.ps1` for a deployment), and a seed only inserts a bundle that is missing. So when a seeded bundle needs new entries, the seeds get them and an EF migration adds them to the bundle a deployment already has, without touching entries an admin added or removed (#531).

### 7.2 Baseline always-allow

Every session implicitly allows the hosts the Microsoft / school sign-in flow navigates through, and nothing more of Microsoft's web (#516):

- `*.microsoftonline.com` — Entra sign-in (`login.`), its password reset (`passwordreset.`) and device sign-in (`device.login.`)
- `login.microsoft.com`, `login.windows.net` — Entra's other sign-in hosts, which some sites still use as authority
- `mysignins.microsoft.com` — where sign-in sends a student who must register security info (MFA, password reset) first
- `login.live.com`, `account.live.com` — personal Microsoft accounts: Entra hands a consumer address to `login.live.com`, which may verify it or reset its password on `account.live.com`
- The dashboard's host(s), taken from the deployment's `Cors:AllowedOrigins` (#409) — so a teacher who runs the agent on their own laptop can still pick bundles and end the session. Students may reach it too; they can't do anything there. The backend host itself needs no entry: the extension filters only top-level navigations, not the dashboard's API calls.

Without these, a student who has to sign in during a session (a site's "Sign in with Microsoft", or the dashboard on a teacher's laptop) is locked out. The agent itself signs in through WAM, outside Edge, so the list doesn't concern it.

What the list leaves out, and why:

- Office, Outlook, Teams, Copilot and the rest of `*.office.com`, `*.office365.com`, `*.microsoft.com` and `*.live.com` come with the **Microsoft 365** bundle (§7.1), so the teacher opts in. The baseline used to carry those wildcards, which opened them in every session.
- `*.windows.net` is Azure's shared hosting namespace (storage, static sites, databases): anyone's site can live there. Its one sign-in host, `login.windows.net`, is listed above.
- Web fonts (`fonts.googleapis.com`, `fonts.gstatic.com`) and the sign-in pages' own scripts and images load as subresources of the page, and so do the hidden frames sign-in uses to renew a token. The extension filters only top-level navigations, so none of them needs an entry.

`contracts/session-baseline.json` holds the same list: Backend CI holds `SessionAllowlist.BaselineDomains` to it, and the extension's end-to-end specs hand it down and check in Edge that the sign-in hosts load while the rest stays blocked.

### 7.3 Enforcement model

Every session is strict: only bundle domains + baseline resolve; everything else is blocked. A "loose" mode (allow-everything-except-known-bad-categories) was prototyped and removed (#88) because the school firewall already covers the same ground.

### 7.4 "Site is broken" feedback loop

The extension's block page includes a "request access" button that pings the dashboard. The teacher sees pending requests during the session and can approve them for the rest of the period in one click. The request log also tells us which bundles need expanding centrally.

Decisions are per student: under each requested site the teacher sees who asked, and approves or denies each of them; approving the site for the whole class is a separate, deliberate choice. A denial takes that student's request off the pending list, and the site's row goes once no student is left on it. It reaches the student the way an approval does (backend → agent → extension), and the block page says the request was declined and offers to ask again. A denial isn't final: a request the student sends after it is pending again, so the list holds what still needs an answer. The backend dates each request as it arrives, so a student laptop's clock can't put a new request before the denial it follows (#523).

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
- **Edge extension** → no sign-in of its own. The agent is its identity: it hands the extension the session over native messaging and reports for it (#489). An earlier design signed the student in through the browser (`chrome.identity.launchWebAuthFlow`); any failure there — another account, a personal one, a closed sign-in window — left the extension unfiltered without anyone seeing it.

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
