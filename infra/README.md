# Anchor — Azure Infrastructure

All resources live in a single resource group. The backend runs on a Basic B1 App Service plan and Azure SQL Standard S0, sized for a school rollout (~€24/month — see [Production tiers and scaling](#production-tiers-and-scaling)); the Static Web App is on the free tier. Realtime (SignalR) runs in-process on the App Service, so there is no separate SignalR resource — see [Realtime: in-process SignalR](#realtime-in-process-signalr). Region defaults to the resource group's region and can be set per resource — see [Regions](#regions).

## Recommended: one-command bootstrap (`scripts/setup.ps1`)

[`scripts/setup.ps1`](../scripts/setup.ps1) stands up a fork's whole cloud
environment end to end: resource group → Entra app registrations (incl. their
service principals) → the Bicep deploy → apply the OBO client secret → fetch the
deployment credentials → write the GitHub Actions secrets/variables the deploy
workflows consume → grant Entra admin consent. This is the **only path you need
for an automated install** — the alternatives further down are not extra steps
to run on top of it.

Run it from **PowerShell 7+** (`pwsh`): the guided UX uses PwshSpectreConsole,
which the script bootstrap-installs on first run. A bare `./scripts/setup.ps1`
is interactive (it asks for the suffix, region, repo, etc.); pass the parameters
and `-NonInteractive` for CI.

```powershell
./scripts/setup.ps1                                     # guided: prompts for everything
./scripts/setup.ps1 -UniqueSuffix lincolnhigh -WhatIf   # dry-run: prints the full plan, changes nothing
./scripts/setup.ps1 -UniqueSuffix lincolnhigh           # provision + wire GitHub
```

It is **idempotent and resumable** (see [Re-running / resuming](#re-running--resuming))
and can **adopt an environment that already exists** (see [Adopting an existing
environment](#adopting-an-existing-environment)).

The two **manual alternatives** below are substitutes for this script, not
follow-up steps — reach for them only if you can't run it (e.g. you're not on
Windows / PowerShell) or want to drive the pieces by hand. The Bicep section is
also where every template parameter is documented.

> Requires the [Azure CLI](https://learn.microsoft.com/en-us/cli/azure/install-azure-cli)
> and the [GitHub CLI](https://cli.github.com/), both logged in (`az login`,
> `gh auth login`).

### Entra Graph permissions

The script grants each app registration the **delegated Microsoft Graph** scopes
it needs and then admin-consents them in Step 8 (best-effort — it prints the
manual `az ad app permission admin-consent` command when the runner isn't a
tenant admin):

| App | Delegated scope | Why |
|---|---|---|
| Dashboard SPA | `User.Read` | baseline sign-in / profile so users can log in. |
| API | `User.Read.All` | the on-behalf-of (OBO) user-directory search behind **Classes → School** calls Graph `/users?$select=companyName`, which the basic profile can't read. Without this scope the OBO exchange fails and `GET /directory/schools` returns **502**, leaving the School selector empty (#281). |

Both grants are idempotent (skipped when already present). When the script
**adopts** an API registration it doesn't own (`-EntraClientId`), it won't mutate
it but flags a missing `User.Read.All` as a `[MANUAL]` line with the exact grant +
consent commands.

### Regions

`-Location` sets the default region for the resource group and every resource.
Override individual resources with `-SqlLocation`, `-AppServiceLocation`,
`-StaticWebAppLocation` (each falls back to `-Location`). These map straight to
the matching Bicep parameters.

- **Why per-resource:** a single region rarely fits. **Static Web Apps** are
  offered only in a limited set of regions, so the dashboard may need to live
  apart from your SQL/App Service region. The live `anchor-rg` (`arcadia`)
  deployment is itself split — App Service / plan / SQL in **Belgium Central**,
  Static Web App in **West Europe**.
- **Adopt-in-place:** when a resource already exists, the script reads its
  current region and pins it (region is immutable in Azure — a redeploy that
  tried to move it would fail), so you never have to specify regions just to
  re-run against an existing environment.

### Re-running / resuming

Re-running **is** the resume mechanism: every step reads live Azure/Entra state
and only changes what's missing, so a run interrupted by a timeout converges on
the next run rather than duplicating work. Specifically:

- Entra apps are looked up before creating; the `access_as_user` scope id and
  the `anchor-obo` client secret are reused if already present (the scope id
  stays stable so prior admin consent isn't invalidated).
- The Bicep deploy is declarative (ARM converges to the template).
- GitHub secrets/variables are upserts.
- A not-yet-created Static Web App / App Service is tolerated: the affected
  GitHub secret is skipped (with a warning) instead of failing the run.

**One caveat:** an Entra client secret can only be read at creation, so a run
interrupted *after* minting the secret but *before* you copied it cannot
re-display it — reset it manually (`az ad app credential reset`) if needed.

Use `-SkipInfra` to skip the Bicep deploy entirely and only (re-)wire GitHub
against an environment that already exists.

### Adopting an existing environment

To point the script at a hand-built environment (like the original `arcadia`
one) without disturbing it, pass the real app-registration ids:

```powershell
./scripts/setup.ps1 -UniqueSuffix arcadia `
  -EntraClientId <api-app-guid> -SpaClientId <spa-app-guid> -WhatIf
```

With an id supplied (or discoverable from the App Service's existing
`AzureAd__ClientId`), the script **adopts** that registration: it reuses it and
skips the create/scope/secret mutations, so a working API is never repointed at
a freshly-created app. It also pins each existing resource's region. Supply the
**current** SQL admin password — the deploy always passes it, so a different
value would reset it.

> The live `arcadia` resources currently sit on a **disabled subscription**;
> `az` write/action calls (including `az webapp config appsettings list`) are
> blocked until it is re-enabled, so pass `-EntraClientId` explicitly there
> rather than relying on app-setting discovery.

## Alternative: deploy with Bicep directly (no script)

> Equivalent to the deploy step inside `scripts/setup.ps1`, minus the Entra,
> credential-fetch and GitHub-wiring steps — use it only if you're driving those
> by hand or can't run the script. Doubles as the reference for every template
> parameter (see [Parameters](#parameters) below).

Requires the [Azure CLI](https://learn.microsoft.com/en-us/cli/azure/install-azure-cli).

```bash
# 1. Log in
az login

# 2. Create the resource group
az group create --name anchor-rg --location westeurope

# 3. Deploy everything
az deployment group create \
  --resource-group anchor-rg \
  --template-file infra/main.bicep \
  --parameters sqlAdminPassword='<pick-a-strong-password>' \
               entraTenantId='<your-tenant-guid>' \
               entraClientId='<your-api-app-client-guid>'
```

The deployment takes ~3 minutes and outputs the resource names + URLs for the
App Service, SQL Server, and Static Web App, plus the Entra/CORS values it
applied — everything the fork bootstrap (`scripts/setup.ps1`) consumes.

### Parameters

Every name and identifier is a parameter, so a fork stands up its own
environment from parameters alone (no `arcadia` assumptions). The **name**
defaults reproduce the live `anchor-rg` deployment, so the original environment
still deploys with the same names. **Regions are not hardcoded**: `location`
defaults to the resource group's region and each resource can override it (the
live `arcadia` env is itself split across two regions — see
[Regions](#regions)), so a no-arg redeploy will not try to move an existing
resource only when you pass the matching per-resource locations (the
`scripts/setup.ps1` bootstrap reads and pins them for you).

| Parameter | Default | Purpose |
|---|---|---|
| `uniqueSuffix` | `arcadia` | Suffix for globally-unique names; drives the resource-name defaults below. |
| `location` | resource group region | Default region for all resources. |
| `sqlServerLocation` / `appServiceLocation` / `staticWebAppLocation` | `location` | Per-resource region overrides (App Service plan follows `appServiceLocation`). |
| `sqlServerName` / `sqlDatabaseName` | `anchor-sql-<suffix>` / `anchordb` | SQL logical server + database name. |
| `appServiceName` / `appServicePlanName` | `anchor-api-<suffix>` / `ASP-anchorrg-b49b` | Backend App Service + plan name. |
| `staticWebAppName` | `anchor-dashboard` | Dashboard SWA name. |
| `entraTenantId` / `entraClientId` | empty | Entra tenant + API app-registration client ID. Required for a working deploy; applied as App Service settings (`AzureAd__TenantId` / `AzureAd__ClientId`). |
| `entraAudience` | `api://<entraClientId>` | JWT audience the API validates. |
| `entraInstance` | current cloud login endpoint | Entra authority. |
| `dashboardCorsOriginOverride` | empty → deployed SWA URL | Allowed CORS origin for the dashboard SPA (`Cors__AllowedOrigins__0`). |
| `sqlAdminLogin` / `sqlAdminPassword` | `anchoradmin` / *(required, secure)* | SQL admin credentials. |

Bicep applies the Entra IDs and CORS origin as **App Service application
settings** (double-underscore form), so the deployed API gets its
environment-specific config from the infra rather than from committed
`appsettings.json` (pairs with the config-externalization work, issue #201).

To tear it all down:

```bash
az group delete --name anchor-rg --yes
```

#### After teardown — what survives, and recreating

Deleting the resource group removes the Azure resources but **not** everything
the environment depends on. The B1 plan and the S0 database bill by the hour,
so deleting stops their charges and recreating costs nothing extra (the
database's data is gone, of course) — but mind these:

- **Entra app registrations and their admin consent live in Entra ID, not in
  the resource group**, so `az group delete` leaves them untouched (you won't
  see them in Resource Manager — they're under Entra ID → App registrations).
  **Don't delete them.** On the next run `scripts/setup.ps1` reuses them (looked
  up by display name, or pass `-EntraClientId` / `-SpaClientId`), and the admin
  consent you granted still holds — so you skip that manual step. You only need
  to re-consent if you delete/recreate the apps or a new permission is added.
- **GitHub secrets/variables are not touched** (they live in the repo), but the
  `AZURE_STATIC_WEB_APPS_API_TOKEN` secret and the backend OIDC variables
  (`AZURE_CLIENT_ID` / `AZURE_TENANT_ID` / `AZURE_SUBSCRIPTION_ID`, plus the
  Website Contributor role assignment on the App Service) are **bound to the
  deleted resources** — they go stale. Re-run `scripts/setup.ps1` to recreate the
  deploy identity + role assignment and refetch the SWA token; deploys will fail
  to authenticate in the gap. A recreated Static Web App may also get a **new
  default hostname**, which invalidates the SPA redirect URI — the script
  rewrites it from the new SWA URL on each run.
- **The SQL admin password cannot be read back from Azure.** If you didn't save
  it, you can't recover it — set a fresh one on the recreate (the script prompts
  for it and the deploy applies it).

#### Dashboard returns 404 after publish (deploy-token authentication)

Symptom: the build leg of `Dashboard CI / Deploy` passes, but the **Deploy to
Azure Static Web Apps** step fails with `deployment_token provided was invalid`,
so nothing is published and the dashboard URL returns a bare **404** (issue
#272). The GitHub secret is write-only, so a wrong value isn't visible in the UI
— **check the failed run's logs, not the secret.** Two causes have actually bitten:

1. **The secret holds a bad value.** `gh secret set` reads the value from
   **stdin when `--body` is omitted**; passing `--body -` writes the *literal*
   string `-` (gh does not treat `-` as a stdin sentinel). That silently wrote
   `-` into `AZURE_STATIC_WEB_APPS_API_TOKEN`. Re-sync from Azure's live token —
   note the corrected `gh` invocation (pipe to stdin, **no** `--body -`):

   ```bash
   az staticwebapp secrets list -n anchor-dashboard -g anchor-rg \
     --query properties.apiKey -o tsv \
     | gh secret set AZURE_STATIC_WEB_APPS_API_TOKEN --repo plinklabs/Anchor

   gh run rerun <run-id> --failed   # re-run the failed deploy with the new secret
   ```

   `scripts/setup.ps1` refetches and re-sets this secret on every run (the token
   is **not** a Bicep output, so an infra-only `az deployment` does not re-sync
   it) — re-running the bootstrap fixes it too.

2. **The SWA instance itself won't serve.** If the token is valid (a manual
   `swa deploy` succeeds) yet every path still 404s with no SWA response headers
   (`ETag` / `Strict-Transport-Security` absent), the instance's content layer is
   stuck. Confirm by deploying the *same* build to a throwaway SWA
   (`az staticwebapp create -n anchor-dashboard-probe -g anchor-rg -l westeurope
   --sku Free`) — if that serves 200, recreate `anchor-dashboard`. The Free tier
   hands out a **new random hostname**, so re-point the Entra SPA redirect URI
   and the API's `Cors__AllowedOrigins__0` to it (re-running `scripts/setup.ps1`
   does both), then re-run the deploy.

### Production tiers and scaling

The template provisions the tiers a school rollout needs: ~1,000 students, ~300
of them in a session at any time during school hours (#341).

- **App Service plan: Basic B1 (Linux), with Always On.** F1 caps a Linux app
  at 5 concurrent WebSockets and 60 CPU-minutes a day, and every student holds
  two connections (agent + extension). B1 allows ~50k WebSockets per instance,
  which covers the ~1,600 peak connections with in-process SignalR. Always On
  keeps the process loaded between requests, so the background services
  (`HeartbeatMonitor`, `EventPruner`, `SessionAutoEnder`) keep running; F1
  doesn't offer it.
- **Azure SQL: Standard S0 (10 DTU), `maxSizeBytes` 250 GB.** Serverless only
  pays off while the database sleeps most of the time, but every agent or
  extension (re)connect resolves the user in the database, so with students
  connected it stays awake through the school day (~€70–240/month at minimum
  capacity). S0 is a flat price. Estimated storage at rollout scale is ~0.35 GB
  of raw events (14-day retention, and foreground changes no longer carry window
  titles or paths, #345) plus ~0.2 GB per school year of session summaries and
  participants, well inside the 250 GB S0 includes. Sessions a teacher forgets
  to end are ended four hours after they started, so their events are pruned
  too. The template sets `maxSizeBytes` explicitly: without it, a deploy applies
  the tier's default max size (the serverless database got 32 GB that way).

**Load test before go-live** with ~300 simulated students (agent + extension
heartbeats, ~40 foreground changes per student per hour) to confirm B1 + S0 —
tracked in #346. If the database runs out of DTUs, move to **S1** (~€31.60/month):
change the `sku.name` of `sqlDb` in `main.bicep` and redeploy. Moving between
S0, S1 and S2 is an online operation, and all three include 250 GB. Change the
template rather than only running `az sql db update`, or the next redeploy
scales the database back down.

### Realtime: in-process SignalR

The SignalR hub runs inside the API process (`AddSignalR()` in
`backend/src/Anchor.Api/Program.cs`), so agents, extensions and the dashboard
connect straight to the App Service. One B1 instance covers that (see above).
The template provisions no Azure SignalR Service, and a bigger class doesn't
need one (#343).

The service only becomes relevant if the backend scales out to more than one
instance: then each instance only reaches the clients connected to it, unless
the API switches to `AddAzureSignalR()` or a backplane. Scale-out also needs
shared heartbeat state, because `HeartbeatTracker` and `ActiveParticipantCache`
live in memory. Add the SignalR resource and its
`Azure__SignalR__ConnectionString` app setting back together with that code
change; until then, infra CI fails a template that provisions them.

**Environments deployed before #343** still have an `anchor-signalr` resource
(Free tier, unused). A redeploy leaves it in place, because the deploy doesn't
delete resources the template no longer declares, but it drops the
`Azure__SignalR__ConnectionString` setting from the App Service. Delete the
resource when convenient:

```bash
az signalr delete --name anchor-signalr --resource-group anchor-rg
```

---

## Alternative: manual setup via the Azure Portal

> Also a substitute for the script, not a follow-up. Use it if the CLI gives you
> trouble (TPM errors, etc.) — create each resource manually in the portal.
> Everything goes into one resource group.

### 1. Resource group

- Go to **Resource Groups** → Create
- Name: `anchor-rg`
- Region: `West Europe`

### 2. SQL Database

- Search **"SQL databases"** → Create
- Database name: `anchordb`
- Server: **Create new**
  - Server name: `anchor-sql-yourschool` (must be globally unique)
  - Location: West Europe
  - Authentication: SQL authentication
  - Admin login + password — save these somewhere safe
- Elastic pool: No
- Workload environment: **Production**
- Compute + storage → click **Configure database**:
  - Service tier: **Standard (DTU-based)**
  - DTUs: **S0 (10 DTUs)**
  - Data max size: **250 GB** (included in S0 — see [Production tiers and scaling](#production-tiers-and-scaling))
- Backup storage redundancy: **Locally-redundant**
- **Networking** tab:
  - Connectivity method: Public endpoint
  - Toggle **"Allow Azure services and resources to access this server"**: Yes

### 3. App Service

- Search **"App Services"** → Create → **Web App**
- Name: `anchor-api-yourschool` (must be globally unique)
- Publish: **Code**
- Runtime stack: **.NET 10 (LTS)** — must match the backend's target framework
  (`net10.0`, the `DOTNETCORE|10.0` that `main.bicep` sets); a build on a host
  pinned to an older runtime deploys fine but answers 503
- OS: **Linux**
- Region: West Europe
- Pricing plan: Create new → **Basic B1**

After creation, go to the app → **Settings → Configuration → General settings**
and turn **Always on** to **On** (it keeps the background services running).

Then go to **Settings → Environment variables**:

Add a **connection string**:
- Name: `DefaultConnection`
- Type: SQL Azure
- Value: `Server=tcp:YOUR-SQL-SERVER.database.windows.net,1433;Database=anchordb;User ID=YOUR-ADMIN;Password=YOUR-PASSWORD;Encrypt=true;TrustServerCertificate=false;`

There is no SignalR Service to create: realtime runs in-process on this App
Service (see [Realtime: in-process SignalR](#realtime-in-process-signalr)).

### 4. Static Web App

- Search **"Static Web Apps"** → Create
- Name: `anchor-dashboard`
- Plan type: **Free**
- Region: West Europe
- Deployment source: **Other** (connect GitHub later)

---

## Resources created

Default names below assume `uniqueSuffix=arcadia` (the live `anchor-rg` deployment). Override the parameters to stand up a second environment.

| Resource | Type | Tier | Monthly cost |
|---|---|---|---|
| `anchor-rg` | Resource group | — | €0 |
| `anchor-sql-arcadia` | SQL Server (logical) | — | €0 |
| `anchordb` | SQL Database | Standard S0 (10 DTU), 250 GB max | ~€12.60 |
| `anchor-api-arcadia` | App Service | Runs on the plan below, Always On | (in the plan) |
| `ASP-anchorrg-b49b` | App Service Plan | Basic B1, Linux | ~€11.60 |
| `anchor-dashboard` | Static Web App | Free | €0 |

**Total:** ~€24/month (list prices, Belgium Central, excl. VAT), or ~€43/month
if the load test calls for S1 — see [Production tiers and scaling](#production-tiers-and-scaling).
Realtime runs in-process on the App Service, so there is no SignalR line (see
[Realtime: in-process SignalR](#realtime-in-process-signalr)).

---

## Outputs

The deployment emits everything the fork bootstrap (`scripts/setup.ps1`) needs
to populate GitHub secrets/variables, without re-querying Azure:

`resourceGroup`, `location`, `appServiceName`, `appServiceUrl`,
`staticWebAppName`, `swaUrl`, `sqlServerName`, `sqlServerFqdn`,
`sqlDatabaseName`, the resolved per-resource regions (`sqlServerLocation` /
`appServiceLocation` / `staticWebAppLocation`), and the applied
`entraTenantId` / `entraClientId` / `entraAudience` / `dashboardCorsOrigin`.

## What's NOT provisioned here

- **Entra ID app registrations** — the app registrations themselves are created in the Azure AD / Entra portal (or by `scripts/setup.ps1`), not via resource deployment. Their IDs are *passed into* this template (`entraTenantId` / `entraClientId`) and applied as App Service settings.
- **Custom domains** — add later if you want `anchor.yourschool.be` instead of the auto-generated Azure URLs.
- **GitHub Actions deployment** — configure after the backend and dashboard projects exist.
