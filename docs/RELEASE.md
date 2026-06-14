# Release & deployment

This is the operator reference for the **cloud release pipeline** (workstream B of
the [v1 roadmap](../ROADMAP.md)): how a commit to `main` ships the backend API and
the teacher dashboard to Azure, what gates that deploy, and every secret and
variable the pipeline needs. It is the single source of truth for the
required-secrets surface — when a deploy workflow gains or drops a `secrets.*` /
`vars.*` reference, update the inventory here in the same PR.

Client release (the agent via Velopack, the extension via the Edge store) is a
separate workstream. It is tag-triggered, not push-to-`main`, and documented with
each client: the agent in [`agent/README.md`](../agent/README.md) (`agent-v*`
tags → `agent-release.yml`, #209) and the extension in
[`extension/README.md`](../extension/README.md) (`extension-v*` tags →
`extension-release.yml`, #210, incl. the one-time Edge Add-ons developer setup and
its `EDGE_ADDONS_*` config). The cloud secrets/variables inventory below covers
the cloud tier only.

## Pipeline overview

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

## Required secrets and variables

Everything the cloud pipeline consumes, in one place. Set repository **secrets**
under *Settings → Secrets and variables → Actions → Secrets*; set repository
**variables** under the *Variables* tab. A fork must populate these to deploy to
its own Azure; with them unset the deploy workflows fail (secrets) or fall back to
dev defaults (the dashboard `vars.*`, see below).

### GitHub Actions — secrets

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

### GitHub Actions — variables

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
