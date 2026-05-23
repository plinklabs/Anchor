---
name: verify-anchor
description: Run a headless end-to-end verification of the Anchor agent/backend flow. Use AFTER making changes that could affect the agent's connect or session-receive path, or whenever the user asks to "verify" or "smoke test" Anchor. Lets you exercise the full chain (backend up, agent up, POST /sessions, toast triggered) in ~5 seconds without WAM or the Flutter dashboard.
---

# Anchor end-to-end verify

Headless verification of the agent's connect → SessionStarted → toast flow. Replaces the manual cycle (kill agent, sign into WAM, open dashboard, click Start session, watch tray). Used after any change that could affect connectivity, auth, or session handling.

Built on three pieces shipped under #44:
- Backend accepts `X-Dev-Impersonate-Oid` on REST controllers in Development (via `DevImpersonationAuthHandler`).
- Agent accepts `--inject-token` to skip WAM entirely.
- Agent accepts `--status-endpoint <port>` to expose JSON state on loopback.

The actual verification logic lives in `scripts/dev/verify-session-start.ps1` in the repo. This skill is the playbook for using it.

## Preconditions

- Repo: `D:\Anchor` (Windows dev machine).
- `dotnet` on PATH (any recent SDK ≥ 10).
- `appsettings.Development.json` for the agent must set `Dev:ImpersonateOid` to a seeded student OID (default seed is `22222222-2222-2222-2222-222222222222`). The script will warn if it's missing.

## Step 1 — Build if needed

If you're not sure both projects are fresh:

```powershell
dotnet build D:\Anchor\backend\Anchor.sln --nologo -v:q
dotnet build D:\Anchor\agent\FocusAgent.sln -p:Platform=x64 --nologo -v:q
```

If you trust the existing artifacts, you can pass `-SkipBuild` to the script and skip these.

## Step 2 — Make sure the backend is up on port 5276

The script does NOT auto-start the backend (the in-script `Start-Job` approach proved flaky). Start it in the background yourself:

```powershell
$env:ASPNETCORE_ENVIRONMENT='Development'
dotnet run --project D:\Anchor\backend\src\Anchor.Api\Anchor.Api.csproj `
    --no-launch-profile --urls http://localhost:5276
```

(Run it via Bash `run_in_background: true` or PowerShell `run_in_background: true` from the harness — DO NOT block your main shell on it.)

Wait for it to be reachable before invoking the script:

```bash
until curl -sf -o /dev/null -w "%{http_code}" http://localhost:5276 2>/dev/null | grep -qE "^(200|401|404)$"; do sleep 1; done; echo READY
```

## Step 3 — Run the verify script

```powershell
& D:\Anchor\scripts\dev\verify-session-start.ps1 -SkipBuild 2>&1 | Out-String
```

What it does:
1. Confirms backend at `http://localhost:5276` is reachable (401/404 also counts).
2. Launches the agent with `--inject-token --status-endpoint 5295`.
3. Polls `http://127.0.0.1:5295/status` until `connectionStatus == Connected` (≤15 s).
4. GETs `/classes` as the seeded Dev Teacher (impersonation header only) to find the `3A` class id.
5. POSTs `/sessions` for that class id as the Dev Teacher (impersonation header only).
6. Polls the agent's status endpoint until `activeSessionId == <new session id>` (≤5 s).
7. Kills the agent and exits 0 (PASS) or non-zero (FAIL).

Total wall-clock when everything is built: ~5 seconds.

## Step 4 — Interpret the result

- **`END-TO-END VERIFY: PASS`** → the full flow works. Connection + auth + broadcast + agent receipt + toast trigger all green. Ship.
- **FAIL at "Agent did not reach Connected"** → the agent could not connect to the backend. Look at the `lastError` field the script prints — if it mentions a non-2xx HTTP status, auth (DevImpersonation scheme) is rejecting; if it says "Can't reach", backend is actually unreachable (wrong port, firewall, OS-level block).
- **FAIL at "Session created" step** → REST impersonation is broken (or the teacher OID isn't seeded). Check `DevDataSeeder.SeedAsync` ran (look at the backend startup log).
- **FAIL at "Agent did not see SessionStarted"** → broadcast didn't reach the agent's coordinator within 5 s. Either the backend broadcaster has a bug, the agent's hub event subscription is broken (regression on PR #42 territory), or the agent's `SessionCoordinator.ActiveSessionId` plumbing changed.

## Common gotchas

- **Backend on the wrong port.** Backend `launchSettings.json` defaults to **5276**. Dashboard and agent also default to 5276 (since PR #42 / [[reference-agent-dashboard-backend-ports]]). If anything is on a different port, the chain silently routes to a different backend instance and nothing reaches the agent.
- **GDI BitBlt can't capture WinUI toasts** ([[reference-winui3-screenshot-dcomp]]). The verify script doesn't try to screenshot the toast — it polls the agent's status endpoint for `activeSessionId` instead, which is a more reliable signal.
- **A real agent process is locking the build output** when you try to rebuild. `Get-Process -Name FocusAgent.App | Stop-Process -Force` and try again.
- **The agent's `appsettings.Development.json` is gitignored** — it must exist locally with `Dev:ImpersonateOid` populated.

## When NOT to use

- Pure backend changes with no agent interaction → just run `dotnet test backend/Anchor.sln`. Faster, no agent involved.
- UI/visual-only changes to the toast/MainWindow → use `--show-test-toast` + PrintWindow instead (see [[reference-winui3-screenshot-dcomp]]).
- Anything that needs real WAM behaviour or actual Entra token validation → hand off to the user, no headless path exists.
