# Release & deployment

This is the operator reference for **how Anchor ships** (workstream B/C/D of the
[v1 roadmap](../ROADMAP.md)): the two release tiers, how each is triggered, the
versioning/tagging convention, every secret and variable the workflows consume,
and the step-by-step procedure to cut a release. It is the single source of truth
for the required-secrets surface — when any deploy or release workflow gains or
drops a `secrets.*` / `vars.*` reference, update the inventory here in the same PR.

## The hybrid release model

Anchor has **two release tiers**, deliberately decoupled because they ship to
different places at different cadences:

| Tier | What ships | Trigger | Mechanism |
| --- | --- | --- | --- |
| **Cloud** (continuous) | Backend API → Azure App Service; teacher dashboard → Azure Static Web Apps | **push to `main`** (path-filtered) | [`backend-deploy.yml`](../.github/workflows/backend-deploy.yml), [`dashboard-deploy.yml`](../.github/workflows/dashboard-deploy.yml) |
| **Client** (tag-based) | Desktop agent → GitHub Releases via Velopack; Edge extension → Edge Add-ons store | **a version tag** (`agent-v*` / `extension-v*`) | [`agent-release.yml`](../.github/workflows/agent-release.yml) (#209), [`extension-release.yml`](../.github/workflows/extension-release.yml) (#210) |

The split is intentional:

- **Cloud is continuous.** Every commit that lands on `main` and touches the
  backend or dashboard ships immediately — there is no cloud "version", the
  deployed tip *is* the release. Server-side fixes reach all schools without anyone
  cutting anything.
- **Clients are tag-gated.** The agent and the extension are installed on user
  machines / in browsers, so a release is a deliberate, versioned event: bump the
  single version source, commit, push a matching tag. Pushing to `main` never
  ships a client; only a tag does. This keeps the auto-update feed (agent) and the
  store listing (extension) under explicit control.

The rest of this doc covers both tiers: the [cloud pipeline](#cloud-pipeline-overview)
and how it's gated, the [client release tiers](#client-release-tiers), the
[versioning/tagging convention](#versioning-and-tagging), the complete
[secrets/variables inventory](#required-secrets-and-variables) across both tiers,
and [how to cut a release](#cutting-a-release) for each.

## Cloud pipeline overview

The cloud tier deploys **continuously on push to `main`**. Two independent legs,
each path-filtered so an unrelated push never triggers it:

| Leg | Workflow | Trigger | Auth |
| --- | --- | --- | --- |
| Backend API → Azure App Service | [`backend-deploy.yml`](../.github/workflows/backend-deploy.yml) | `workflow_run` after **Backend CI** succeeds on `main` | publish-profile secret |
| Dashboard → Azure Static Web Apps | [`dashboard-deploy.yml`](../.github/workflows/dashboard-deploy.yml) | `push` to `main` under `dashboard/**` | SWA deployment token |

Both target their resource by a **repo Actions variable** (not a hardcoded name),
so a fork deploys to its own `anchor-api-<suffix>` / Static Web App with no source
edits (the #202 convention: non-secret config in `vars.*`, secrets in `secrets.*`).

### How the backend deploy is gated

`backend-deploy.yml` does **not** trigger on `pull_request` or directly on `push`.
It chains off the existing **Backend CI** workflow via `workflow_run`:

```
push to main (backend/**) ─▶ Backend CI (build + test) ─▶ [success?] ─▶ Backend Deploy
```

- `Backend CI` is path-filtered to `backend/**`, so a push that doesn't touch the
  backend never starts the chain.
- `workflow_run` fires for **every** conclusion (success, failure, cancelled) and
  for **any** head branch, so the deploy job re-asserts both conditions in its
  `if`: `conclusion == 'success' && head_branch == 'main'`. A red or cancelled CI
  run, or a successful CI run on another branch, does not deploy.
- The deploy checks out `workflow_run.head_sha`, so the artifact it publishes is
  the exact commit CI validated — not a later tip of `main`.
- EF Core migrations apply on app startup in non-Development environments
  (issue #205), so there is no separate migration step in the workflow.

## Interaction with the PR gate (`ci-gate.yml`)

[`ci-gate.yml`](../.github/workflows/ci-gate.yml) is the single always-present
status check required on `main` and `develop`. It runs on every PR, detects which
areas changed (mirroring each CI workflow's `pull_request` path filters), and
waits only for the suites those changes trigger.

**Adding `backend-deploy.yml` does not change the gate, and that is correct:**

- The gate exists to make path-filtered **PR** checks reliable. `backend-deploy.yml`
  has **no `pull_request` trigger** — it only runs via `workflow_run` on `main`
  after merge. It therefore never produces a check on a PR, so the gate must
  **not** have a `backend_deploy` filter or a wait step for it. A wait on a check
  that never runs would hang the gate forever (the exact failure mode the gate's
  header comment warns about).
- A PR that edits only `.github/workflows/backend-deploy.yml` touches **no gated
  area** (the gate's `backend` filter watches `backend/**` and `backend-ci.yml`,
  not the deploy workflow). The gate passes that PR immediately. This is the
  intended behavior: the deploy workflow's correctness is validated by
  `actionlint`, not by running a live Azure deploy from a fork PR.
- The gate's `backend` filter (`backend/**`, `backend-ci.yml`) and the deploy's
  trigger (`workflow_run: ["Backend CI"]`) reference the **same** Backend CI
  workflow, so a backend change is still fully gated on `main` before it can
  deploy — via Backend CI, which the gate already waits on for PRs and which
  `workflow_run` chains the deploy onto for `main`.

### Branch protection / required-check implications

- **No new required check.** `CI Gate / gate` remains the only required status
  check on `main` and `develop`. Do **not** add `Backend Deploy` (or `Backend CI`)
  as a required check: deploy runs post-merge on `main`, and a required check that
  doesn't run on a PR blocks it permanently.
- **The deploy is a post-merge consequence, not a merge gate.** Protection on
  `main` gates what gets *into* `main`; the deploy then ships whatever landed.
  Branch protection needs no change for this issue.
- The `production` environment on `backend-deploy.yml` (and any reviewers /
  secrets scoped to it) is the place to add a manual approval step later if a
  human gate before backend deploy is ever wanted — that is an environment
  protection rule, not a branch protection rule.

## Client release tiers

The agent and the extension each ship on their **own version tag**, not on a push
to `main`. The deep operator detail for each lives with the client; this section
is the cross-tier summary so a maintainer can cut either from here.

### Desktop agent — Velopack → GitHub Releases (`agent-v*`)

[`agent-release.yml`](../.github/workflows/agent-release.yml) (#209) triggers on a
push of an `agent-v*` tag. It builds the WinUI 3 agent self-contained
(`win-x64`), packages it with **Velopack** (`vpk`), and uploads the
`Setup.exe` + full/delta `.nupkg` + `RELEASES` feed to the **GitHub Release** for
that tag. Installed agents read that feed and auto-update (delta) on the next tag.

- The packaged `appsettings.Production.json` ships as a template with `#{...}#`
  placeholders (#203); pack time substitutes them from the **`AGENT_*` repo
  variables** (see the inventory) so a fork ships an agent pointed at its own
  backend with no source edit — the agent-side mirror of the dashboard's
  `--dart-define` substitution.
- `pack-release.ps1` cross-checks the tag version against the committed
  `<VersionPrefix>` and fails on drift.
- The agent ships **unsigned** (one SmartScreen "More info → Run anyway" on first
  install); code-signing is a planned later upgrade.
- Full detail: [`agent/README.md`](../agent/README.md#versioning).

### Edge extension — Edge Add-ons store (`extension-v*`)

[`extension-release.yml`](../.github/workflows/extension-release.yml) (#210)
triggers on a push of an `extension-v*` tag. It builds the MV3 extension, zips
`dist/` into `artifacts/anchor-extension-<version>.zip`, **always** uploads that
ZIP as a workflow artifact, and — only when the Edge Add-ons submission config is
present — publishes/updates the **single canonical** Edge listing via the Partner
Center API.

- The extension is **backend-agnostic** (it gets its backend URL from the on-box
  agent at runtime, #204), so there is **one** canonical listing (stable ID
  `akkfdaclmpfcnjalcifkcbhgjnnopman`) that every fork reuses. **Forks normally do
  not run this workflow** — it ships the Plink Labs listing.
- If the `EDGE_ADDONS_*` config (see the inventory) is unset, the job still builds
  + packages + uploads the ZIP and prints manual-submit instructions, so the
  listing can be brought up before the API is wired.
- `pack-extension.mjs` cross-checks the tag version against `package.json` and
  fails on drift; the Edge store re-signs on publish (no code-signing here).
- Full detail incl. one-time developer setup:
  [`extension/README.md`](../extension/README.md#publishing-to-the-edge-add-ons-store).

## Versioning and tagging

The cloud tier is **unversioned** — the deployed tip of `main` is the release.
Each **client** has a single version source and a tag format; the two clients
version **independently**.

| Client | Single version source | Tag format | Triggers |
| --- | --- | --- | --- |
| Agent | `<VersionPrefix>` in [`agent/Directory.Build.props`](../agent/Directory.Build.props) (#208) | `agent-v<version>`, e.g. `agent-v1.2.3` | [`agent-release.yml`](../.github/workflows/agent-release.yml) |
| Extension | `version` in [`extension/package.json`](../extension/package.json) (#208) | `extension-v<version>`, e.g. `extension-v1.2.3` | [`extension-release.yml`](../.github/workflows/extension-release.yml) |

- **Agent.** MSBuild auto-imports `Directory.Build.props` into every agent
  project, so the one `<VersionPrefix>` drives `AssemblyVersion`/`FileVersion`, the
  `InformationalVersion` reported on the `/status` endpoint, and the Velopack
  package version. The MSIX `Package.appxmanifest` `<Identity Version>` is kept in
  lockstep (`<VersionPrefix>.0`) by a unit test. The design-system submodule under
  `external/` versions independently by design.
- **Extension.** `package.json` `version` is the single source; the packed
  manifest version is stamped from it and a test locks them together.
- **Tag = release.** Each release workflow derives the package version from the tag
  (`agent-v1.2.3` → `1.2.3`) and the pack script fails the build if that doesn't
  match the committed version source — a tag can never ship a surprising number.

## Required secrets and variables

Everything the release/deploy workflows consume, in one place — **both tiers**.
Set repository **secrets**
under *Settings → Secrets and variables → Actions → Secrets*; set repository
**variables** under the *Variables* tab. A fork must populate the cloud entries to
deploy to its own Azure; with them unset the deploy workflows fail (secrets) or
fall back to dev defaults (the dashboard `vars.*`, see below). The **client-tier**
entries are optional per fork — see [the client section](#client-tier).

### Cloud tier — GitHub Actions secrets

| Name | Used by | What it is / where to get it |
| --- | --- | --- |
| `AZURE_WEBAPP_PUBLISH_PROFILE` | `backend-deploy.yml` | The App Service **publish profile** XML. Azure Portal → the `anchor-api-*` App Service → *Get publish profile* (or `az webapp deployment list-publishing-profiles --xml`). Paste the whole XML as the secret value. Rotate by downloading a fresh profile after resetting publish credentials. |
| `AZURE_STATIC_WEB_APPS_API_TOKEN` | `dashboard-deploy.yml` | The Static Web App **deployment token**. Azure Portal → the Static Web App → *Manage deployment token* (or `az staticwebapp secrets list`). |
| `GITHUB_TOKEN` | `dashboard-deploy.yml`, `ci-gate.yml` | Auto-provided by GitHub Actions; **no setup needed**. Listed only so the inventory is complete. |

> **Hardening note.** The backend publish-profile auth is the simplest path that
> works without an Azure RBAC role assignment. Migrating to **OIDC federated
> credentials** (`azure/login` with a workload-identity federation, no long-lived
> secret) is the planned later upgrade; it removes `AZURE_WEBAPP_PUBLISH_PROFILE`
> in favour of `AZURE_CLIENT_ID` / `AZURE_TENANT_ID` / `AZURE_SUBSCRIPTION_ID`
> repo variables plus a federated credential on the app registration. Tracked as a
> follow-up; the workflow notes it in-file too.

### Cloud tier — GitHub Actions variables

| Name | Used by | What it is | If unset |
| --- | --- | --- | --- |
| `AZURE_WEBAPP_NAME` | `backend-deploy.yml` | App Service name, e.g. `anchor-api-arcadia`. | Deploy fails — `azure/webapps-deploy` has no target. **Required to deploy.** |
| `API_BASE_URL` | `dashboard-deploy.yml` | Backend API base URL baked into the dashboard build (`--dart-define`). | Falls back to the dev default in `lib/main.dart`. |
| `ENTRA_TENANT_ID` | `dashboard-deploy.yml` | Entra tenant ID for dashboard MSAL.js. | Falls back to the dev default in `lib/auth/msal_config.dart`. |
| `ENTRA_CLIENT_ID` | `dashboard-deploy.yml` | Entra **dashboard SPA** app client ID. | Falls back to the dev default. |
| `API_SCOPE` | `dashboard-deploy.yml` | API scope the dashboard requests (e.g. `api://<api-client-id>/.default`). | Falls back to the dev default. |

The four dashboard `vars.*` are **public SPA configuration, not secrets** — they
ship inside the built web bundle and are safe to expose — hence `vars.*` rather
than `secrets.*`. Each independently falls back to the dev default baked into the
source, so a contributor building locally is unaffected.

### Client tier

These drive the **tag-based** agent and extension releases. They are optional per
fork: a fork that only runs its own cloud can ignore them; a fork that ships its
own agent build sets the `AGENT_*` variables; the `EDGE_ADDONS_*` config belongs to
whoever owns the canonical Edge listing (normally Plink Labs only).

#### Agent — variables (`agent-release.yml`)

Non-secret per-deployment config, substituted into the packaged
`appsettings.Production.json` at pack time (#203). Each is a `vars.*` (public
SPA/backend config, not a secret).

| Name | What it is | If unset |
| --- | --- | --- |
| `AGENT_BACKEND_BASE_URL` | Backend API base URL the shipped agent targets. | Substituted as empty → the packaged agent falls back to its template/source default. |
| `AGENT_AUTH_TENANT_ID` | Entra tenant ID for the agent's sign-in. | As above. |
| `AGENT_AUTH_CLIENT_ID` | Entra **agent** app client ID. | As above. |
| `AGENT_AUTH_SCOPE` | API scope the agent requests. | As above. |

#### Extension — Edge Add-ons submission (`extension-release.yml`)

The release workflow submits to the store **only when all three are set**;
otherwise it builds + uploads the ZIP artifact and prints manual-submit
instructions. One-time setup: [`extension/README.md`](../extension/README.md#publishing-to-the-edge-add-ons-store).

| Name | Kind | What it is | If unset |
| --- | --- | --- | --- |
| `EDGE_ADDONS_PRODUCT_ID` | variable | Edge Add-ons **product ID** of the canonical listing. | API submit skipped; ZIP uploaded as artifact for manual submit. |
| `EDGE_ADDONS_CLIENT_ID` | secret | Edge Add-ons **API client ID**. | As above. |
| `EDGE_ADDONS_API_KEY` | secret | Edge Add-ons **API key**. | As above. |

> Both client release workflows use the auto-provided `GITHUB_TOKEN`
> (`agent-release.yml` to upload the Velopack release assets) — no setup needed.

### Azure App Service — application settings

These are **not** GitHub secrets; they are configured **on the App Service itself**
(Portal → *Configuration*, or provisioned by [`infra/main.bicep`](../infra/main.bicep)).
The deployed API reads them at runtime. .NET maps the double-underscore form to its
nested configuration keys (`AzureAd__TenantId` → `AzureAd:TenantId`).

| Setting | Bound to | Notes |
| --- | --- | --- |
| `ConnectionStrings__DefaultConnection` (connection string `DefaultConnection`, type `SQLAzure`) | EF Core / `AnchorDbContext` | Azure SQL connection string. Provisioned by Bicep from the SQL admin login + password. **Required** — the API throws at startup if absent. |
| `AzureAd__TenantId` | `AddMicrosoftIdentityWebApi` | Production Entra tenant. Not committed (blank in `appsettings.json`). **Required.** |
| `AzureAd__ClientId` | `AddMicrosoftIdentityWebApi` | Production **API** app registration client ID. **Required.** |
| `AzureAd__Audience` | JWT bearer validation | Usually `api://<api-client-id>`. **Required.** |
| `AzureAd__ClientCredentials` (e.g. `__0__SourceType`, `__0__ClientSecret`) | OBO token acquisition for Graph directory search | **Required for the user-directory search feature** (the on-behalf-of exchange). Without it the OBO call fails at first use, not at startup. A client secret or certificate on the API app registration. |
| `Cors__AllowedOrigins__0`, `__1`, … | CORS policy | The dashboard origin(s), e.g. the Static Web App URL. **Required** for the dashboard to call the API from the browser. |
| `Azure__SignalR__ConnectionString` | (Azure SignalR, when enabled) | Provisioned by Bicep from the SignalR Service primary key. The API currently uses **in-process** SignalR (`AddSignalR()`), so this is dormant until the backend opts into `AddAzureSignalR()`; documented here because the infra provisions it and it is the App Service setting to populate when that switch happens. |

`Heartbeat`, `EventRetention`, and `Logging` have committed defaults in
`appsettings.json` and only need App Service overrides to tune them — not for a
baseline deploy.

## Cutting a release

Once the inventory above is configured, releasing is routine. Three procedures,
one per shipping unit.

### Cloud (backend / dashboard)

There is no version to bump and no tag to push — **merging to `main` is the
release**.

1. Merge the change to `main` (through the normal PR + `CI Gate / gate` flow).
2. The matching leg deploys automatically:
   - **backend** — Backend CI runs on `backend/**`; on success
     `backend-deploy.yml` publishes the CI-validated commit to the App Service.
     EF Core migrations apply on app startup (non-Development), so there is no
     separate migration step.
   - **dashboard** — a push under `dashboard/**` builds with the `vars.*`
     dart-defines and uploads to the Static Web App.
3. Confirm the deploy succeeded in the Actions tab; the deployed tip is live.

### Agent (`agent-v*`)

1. Bump `<VersionPrefix>` in [`agent/Directory.Build.props`](../agent/Directory.Build.props)
   (the single version source). Commit it on `main`.
2. Push a matching tag:
   ```bash
   git tag agent-v<version>      # e.g. agent-v1.2.3 — must equal <VersionPrefix>
   git push origin agent-v<version>
   ```
3. `agent-release.yml` builds, packs with Velopack (cross-checking the tag against
   the committed version), and publishes `Setup.exe` + the delta-update feed to the
   GitHub Release for the tag. Installed agents auto-update from that feed.

A version/tag mismatch fails the pack step — fix `<VersionPrefix>` or the tag and
re-push.

### Extension (`extension-v*`)

> Normally only the canonical Plink Labs listing is published; forks rely on it
> (the extension is backend-agnostic, #204) and don't run this.

1. Bump `version` in [`extension/package.json`](../extension/package.json) (the
   single version source). Commit it on `main`.
2. Push a matching tag:
   ```bash
   git tag extension-v<version>      # e.g. extension-v1.2.3 — must equal package.json
   git push origin extension-v<version>
   ```
3. `extension-release.yml` builds + packages the ZIP (cross-checking the tag) and,
   when the `EDGE_ADDONS_*` config is set, publishes/updates the canonical Edge
   listing. If it isn't set, download the `anchor-extension-<version>` artifact and
   upload it by hand at the Edge Add-ons dashboard.

## Operator checklist (fork bringing up its own cloud)

1. Provision Azure resources — [`infra/main.bicep`](../infra/main.bicep) (App
   Service, Azure SQL, SignalR, Static Web App). See [infra/README.md](../infra/README.md).
2. Configure the **App Service application settings** above (Entra IDs, CORS
   origins, client credentials). Bicep wires the SQL connection string and SignalR
   connection string for you.
3. Add the GitHub **secrets**: `AZURE_WEBAPP_PUBLISH_PROFILE`,
   `AZURE_STATIC_WEB_APPS_API_TOKEN`.
4. Add the GitHub **variables**: `AZURE_WEBAPP_NAME`, and the four dashboard
   `API_BASE_URL` / `ENTRA_TENANT_ID` / `ENTRA_CLIENT_ID` / `API_SCOPE`.
5. Confirm `CI Gate / gate` is the required status check on `main` (and `develop`).
   No other check should be required.
6. Push a backend or dashboard change to `main` → the matching deploy leg runs
   automatically.
