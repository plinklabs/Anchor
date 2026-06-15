# Anchor Roadmap

This file tracks larger directional work that goes beyond the current release. Day-to-day work lives in GitHub issues; this is for the strategic shape.

## v1 — Release automation & fork-friendly self-host (in progress)

Goal: a commit to `main` ships the cloud; a version tag ships the client; a contributor can fork and stand up their own environment with one bootstrap script (and a documented manual fallback).

- **Hybrid trigger.** Cloud (backend API + DB migrations + dashboard) deploys continuously on push to `main`. Agent + extension publish on version tags / GitHub Releases.
- **Agent** ships unpackaged via [Velopack](https://velopack.io) — free, no code-signing cert, delta auto-update from GitHub Releases.
- **Extension** is published once to the Edge Add-ons store as a single canonical listing; it receives its backend URL from the agent at runtime, so forks reuse it rather than publishing their own. The agent force-installs it via a per-user Edge policy, with a guided-install fallback.
- **Fork = contributor path.** A forker runs `scripts/setup.ps1` (Bicep + Entra app registrations + App Service settings + GitHub secrets) or follows `docs/SETUP.md` manually.

Tracked in the **A–D** issue series (config externalization → cloud pipeline → client release → fork bootstrap).

## v2 — Canonical, tenant-aware school onboarding (deferred)

Goal: make **schools** first-class operators who *never fork*. Forking remains the path for **contributors** (people modifying the code); schools instead consume published artifacts and stand up only what must hold their own data.

### Why a separate model

Forking + "configure Azure" is too much to ask of school IT. But this app records minors' browsing activity, so that data should stay in the **school's own tenant/Azure** — a central multi-tenant SaaS is the wrong call. The result is a deliberate split:

- **Contributors** → fork the repo (v1 path).
- **Schools** → consume the **canonical agent** (Velopack/Releases) + **canonical extension** (Edge store), deploy only their **backend + DB (+ SignalR)** to their own Azure, and connect the clients via discovery.

### Building blocks

- **Canonical clients.** The agent and extension are single published artifacts shared by every school — no per-school builds. (The extension is already canonical in v1; v2 extends the same idea to the agent.)
- **Multi-tenant Entra app.** One upstream-owned app registration. Each school admin grants **admin consent** (one click) instead of creating their own registrations — this removes the hardest manual step in onboarding.
- **DNS-keyed backend discovery.** The student signs in with their school account, so the agent knows the email domain from the token. It resolves a well-known record the school published on their own domain:

  ```
  _anchor.<school-domain>   TXT   "backend=https://anchor-api-<school>.azurewebsites.net"
  ```

  DNS *is* the distributed registry — no upstream server to run — and domain ownership *is* the authority to advertise a backend, so there's no tenant-hijacking problem to design around.
- **Setup-link fallback.** For schools that can't easily publish DNS (or use a vanity / `onmicrosoft.com` domain), the agent registers an `anchor://` protocol and accepts a self-describing setup link:

  ```
  anchor://setup?backend=https://anchor-api-<school>.azurewebsites.net
  ```

  The link *is* the payload — one student click, still no central lookup.
- **Deploy to Azure button.** A public Bicep/ARM template the portal runs from a form — schools stand up their backend tier without forking, local tooling, or the CLI.
- **Optional canonical dashboard.** One upstream-hosted dashboard that discovers the school's backend the same way, so schools deploy only the backend tier.

### Known cost

Canonical clients talk to school backends that update on their own schedule, so v2 requires **API versioning discipline** to manage client/backend version skew. (Under v1's fork model, client and backend version together, so this doesn't arise.)

### Sequencing

v2 builds on v1 — the per-school backend/DB, the Velopack agent, and the canonical extension are all load-bearing here. Decompose into concrete issues when v2 begins.
