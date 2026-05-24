---
name: verify-anchor
description: Verify that an Anchor agent change actually works in the real product, not just in unit tests. Use AFTER any agent change (UI, connection, session, heartbeat, tray, settings — anything observable from outside the process). Picks a mode based on the change: headless smoke script for connection/session-broadcast paths, run-and-observe (with PrintWindow screenshots) for UI/visual changes, log inspection for diagnostic paths. If the current headless tooling cannot reach the scenario you changed, extending the tooling is part of the current PR — not a follow-up issue.
---

# Verify an Anchor agent change

Unit tests prove the code is internally consistent. They do not prove the feature actually shows up in the running product. For any agent change, the bar is: *I observed the behavior in a running agent, not just a green test.* This skill is the playbook for clearing that bar quickly.

## Step 1 — Pick a mode

Match the type of change you made:

| Change type | Mode | Why |
|---|---|---|
| Connection, session lifecycle, hub broadcast, agent receipt of server events | **Headless smoke** (Step 2) | Existing script exercises the full chain in ~5 s; no WAM, no dashboard. |
| UI / visual surfaces (toast, MainWindow, tray, settings dialog, freshness indicator, animations) | **Run-and-observe** (Step 3) | Tests can't see DComp surfaces. Must launch and screenshot. |
| Logging, error handling, diagnostic output, background timers without UI | **Run + log inspection** (Step 4) | Launch the agent, drive the scenario, grep the log. |
| Auth via real WAM, real Entra token validation | **Hand off to user** | No headless path. Tell the user what to click and what to watch for. |

If your change spans modes (e.g. heartbeat fix that affects both a hub call AND the MainWindow dot color), do both — headless to confirm the chain, run-and-observe to confirm the UI surface.

## Step 2 — Headless smoke (`verify-session-start.ps1`)

Covers: backend up → agent up → POST `/sessions` → agent reaches `Connected` → agent receives `SessionStarted` → `activeSessionId` is set on agent.

Built on three pieces shipped under #44:
- Backend accepts `X-Dev-Impersonate-Oid` on REST controllers in Development (via `DevImpersonationAuthHandler`).
- Agent accepts `--inject-token` to skip WAM entirely.
- Agent accepts `--status-endpoint <port>` to expose JSON state on loopback.

### Preconditions

- Repo: `D:\Anchor` (Windows dev machine).
- `dotnet` on PATH (SDK ≥ 10).
- `agent/.../appsettings.Development.json` sets `Dev:ImpersonateOid` to a seeded student OID (default seed `22222222-2222-2222-2222-222222222222`). File is gitignored — must exist locally.

### Build if needed

```powershell
dotnet build D:\Anchor\backend\Anchor.sln --nologo -v:q
dotnet build D:\Anchor\agent\FocusAgent.sln -p:Platform=x64 --nologo -v:q
```

Pass `-SkipBuild` to the script if you trust the existing artifacts.

### Start backend (background)

The script does NOT auto-start the backend; the in-script `Start-Job` approach proved flaky. Start it yourself with `run_in_background: true`:

```powershell
$env:ASPNETCORE_ENVIRONMENT='Development'
dotnet run --project D:\Anchor\backend\src\Anchor.Api\Anchor.Api.csproj `
    --no-launch-profile --urls http://localhost:5276
```

Wait for the port before invoking the script:

```bash
until curl -sf -o /dev/null -w "%{http_code}" http://localhost:5276 2>/dev/null | grep -qE "^(200|401|404)$"; do sleep 1; done; echo READY
```

### Run

```powershell
& D:\Anchor\scripts\dev\verify-session-start.ps1 -SkipBuild 2>&1 | Out-String
```

The script confirms backend reachability, launches the agent with `--inject-token --status-endpoint 5295`, polls until `connectionStatus == Connected`, GETs `/classes` as the Dev Teacher to find the `3A` class id, POSTs `/sessions`, polls the agent's status endpoint until `activeSessionId == <new session id>`, then kills the agent and exits.

### Interpret

- **`END-TO-END VERIFY: PASS`** → connect + auth + broadcast + agent receipt all green. Ship.
- **FAIL at "Agent did not reach Connected"** → look at `lastError`. Non-2xx HTTP = `DevImpersonation` auth rejecting; "Can't reach" = backend unreachable (wrong port, firewall).
- **FAIL at "Session created"** → REST impersonation broken or teacher OID not seeded. Check `DevDataSeeder.SeedAsync` ran (backend startup log).
- **FAIL at "Agent did not see SessionStarted"** → broadcast didn't reach the agent's coordinator within 5 s. Either backend broadcaster bug, agent hub event subscription regression (PR #42 territory), or `SessionCoordinator.ActiveSessionId` plumbing changed.

## Step 3 — Run-and-observe (UI changes)

Launch the agent yourself and screenshot the surface you changed. Two approaches:

### Visual-only changes (toast, indicator state, animations)

If the agent has a `--show-test-toast`-style flag for your surface, use it — no real session needed. Otherwise launch the agent with `--inject-token` and drive it through the headless script in Step 2 to trigger the scenario, then screenshot.

For screenshots: **WinUI 3 surfaces require PrintWindow with `PW_RENDERFULLCONTENT`, not BitBlt** ([[reference-winui3-screenshot-dcomp]]). DComp surfaces are invisible to GDI; a BitBlt of the toast region returns blank.

### State-change UI (dot color, freshness label, in-session badge)

These need both the *normal* and the *changed* state captured. Example: heartbeat staleness needs (a) green dot while backend is up, then (b) red `(stale)` after backend is killed. Script the transition — don't rely on eyeballing.

## Step 4 — Run + log inspection

For changes that have no UI surface but should produce a specific log signal:

1. Launch the agent normally (or with `--inject-token` to skip WAM).
2. Drive the scenario.
3. Grep the agent's log file (or stdout if launched from a shell). Use `Monitor` with a `tail -f | grep` if you need a live signal.

## When the headless tooling can't reach your scenario

This is the important rule: **extending the tooling is part of the current change, not a follow-up issue.**

If you're fixing or shipping something that the current verify-script can't headlessly exercise — for example, heartbeat staleness needs a `lastHeartbeatAt` field on `/status` plus a verify script that kills/restarts the backend — then:

1. Add the agent-side hook (new `/status` field, new CLI flag, new test surface) **in the same PR** as the feature change.
2. Add the verify-script branch (or new script under `scripts/dev/`) **in the same PR**.
3. Wire the new verify into your PR description's test plan.

The PR that ships a feature also ships the way to verify it. This keeps the headless tooling growing in step with the product and prevents an ever-growing "things I had to verify manually" debt.

**Carve-out:** if the extension is genuinely large (a new mode that's basically its own feature — say, full snapshot-diffing of the agent's tray menu), call that out to the user explicitly *before* deciding. Default is fold-in; separate issue needs justification.

## Common gotchas

- **Backend on the wrong port.** Backend `launchSettings.json` defaults to **5276**. Dashboard and agent also default to 5276 (since PR #42 / [[reference-agent-dashboard-backend-ports]]). Port mismatch silently routes to a different backend instance and nothing reaches the agent.
- **GDI BitBlt can't capture WinUI toasts** ([[reference-winui3-screenshot-dcomp]]). The verify script doesn't screenshot — it polls `/status` for `activeSessionId`. If you need a screenshot, use PrintWindow.
- **A real agent process is locking the build output** when you try to rebuild. `Get-Process -Name FocusAgent.App | Stop-Process -Force` and try again.
- **The agent's `appsettings.Development.json` is gitignored** — must exist locally with `Dev:ImpersonateOid` populated.

## When NOT to use

- Pure backend changes with no agent interaction → just run `dotnet test backend/Anchor.sln`.
- Pure unit-level agent logic with no external surface (a private helper, a pure-function refactor) → tests are sufficient. The bar is "did the observable behavior change?" If nothing observable changed, no verify needed.
