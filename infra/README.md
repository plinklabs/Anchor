# Anchor — Azure Infrastructure

All resources live in a single resource group. Everything starts on free tiers; upgrade SignalR to Standard when you test with a real class (20+ students). Region defaults to the resource group's region and can be set per resource — see [Regions](#regions).

## Recommended: one-command bootstrap (`scripts/setup.ps1`)

[`scripts/setup.ps1`](../scripts/setup.ps1) stands up a fork's whole cloud
environment end to end: resource group → Entra app registrations (incl. their
service principals) → the Bicep deploy → apply the OBO client secret → fetch the
deployment credentials → write the GitHub Actions secrets/variables the deploy
workflows consume → grant Entra admin consent. This is the **only path you need
for an automated install** — the alternatives further down are not extra steps
to run on top of it.

```powershell
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
`-SignalRLocation`, `-StaticWebAppLocation` (each falls back to `-Location`).
These map straight to the matching Bicep parameters.

- **Why per-resource:** a single region rarely fits. **Static Web Apps** and
  **SignalR** are offered only in a limited set of regions, so they may need to
  live apart from your SQL/App Service region. The live `anchor-rg` (`arcadia`)
  deployment is itself split — App Service / plan / SQL in **Belgium Central**,
  SignalR / Static Web App in **West Europe**.
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
App Service, SignalR, SQL Server, and Static Web App, plus the Entra/CORS values
it applied — everything the fork bootstrap (`scripts/setup.ps1`) consumes.

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
| `sqlServerLocation` / `appServiceLocation` / `signalrLocation` / `staticWebAppLocation` | `location` | Per-resource region overrides (App Service plan follows `appServiceLocation`). |
| `sqlServerName` / `sqlDatabaseName` | `anchor-sql-<suffix>` / `anchordb` | SQL logical server + database name. |
| `appServiceName` / `appServicePlanName` | `anchor-api-<suffix>` / `ASP-anchorrg-b49b` | Backend App Service + plan name. |
| `signalrName` / `staticWebAppName` | `anchor-signalr` / `anchor-dashboard` | SignalR + dashboard SWA name. |
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
the environment depends on. All resources are free-tier, so deleting and
recreating costs nothing — but mind these:

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

### Upgrading SignalR for pilot

When you need more than 20 connections, change the SKU in `main.bicep`:

```bicep
sku: {
  name: 'Standard_S1'
  capacity: 1           // 1 unit = 1000 connections
}
```

Then redeploy with the same command.

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
- Workload environment: **Development**
- Compute + storage → click **Configure database**:
  - Service tier: **General Purpose**
  - Compute tier: **Serverless**
  - Min vCores: 0.5
  - Max vCores: 2
  - Auto-pause delay: 60 minutes
  - Check **"Use free limit"** if the option appears
- Backup storage redundancy: **Locally-redundant**
- **Networking** tab:
  - Connectivity method: Public endpoint
  - Toggle **"Allow Azure services and resources to access this server"**: Yes

### 3. App Service

- Search **"App Services"** → Create → **Web App**
- Name: `anchor-api-yourschool` (must be globally unique)
- Publish: **Code**
- Runtime stack: **.NET 8 (LTS)**
- OS: **Linux**
- Region: West Europe
- Pricing plan: Create new → **Free F1**

After creation, go to the app → **Settings → Environment variables**:

Add a **connection string**:
- Name: `DefaultConnection`
- Type: SQL Azure
- Value: `Server=tcp:YOUR-SQL-SERVER.database.windows.net,1433;Database=anchordb;User ID=YOUR-ADMIN;Password=YOUR-PASSWORD;Encrypt=true;TrustServerCertificate=false;`

### 4. SignalR Service

- Search **"SignalR Service"** → Create
- Name: `anchor-signalr`
- Region: West Europe
- Pricing tier: **Free** (20 connections — dev only)
- Service mode: **Default**

After creation:
1. Go to **Keys**, copy the **primary connection string**
2. Go to your App Service → **Environment variables** → add app setting:
   - Name: `Azure__SignalR__ConnectionString` (double underscores)
   - Value: the connection string you just copied

### 5. Static Web App

- Search **"Static Web Apps"** → Create
- Name: `anchor-dashboard`
- Plan type: **Free**
- Region: West Europe
- Deployment source: **Other** (connect GitHub later)

---

## Resources created

Default names below assume `uniqueSuffix=arcadia` (the live `anchor-rg` deployment). Override the parameters to stand up a second environment.

| Resource | Type | Tier | Monthly cost (dev) |
|---|---|---|---|
| `anchor-rg` | Resource group | — | €0 |
| `anchor-sql-arcadia` | SQL Server (logical) | — | €0 |
| `anchordb` | SQL Database | GP Serverless, 0.5–2 vCores | €0 (free limit) |
| `anchor-api-arcadia` | App Service | F1 Free | €0 |
| `ASP-anchorrg-b49b` | App Service Plan | F1 Free, Linux | €0 |
| `anchor-signalr` | SignalR Service | Free | €0 |
| `anchor-dashboard` | Static Web App | Free | €0 |

**Pilot cost** (Standard SignalR): ~€45/month for SignalR + ~€5–15/month for SQL if it exceeds the free limit.

---

## Outputs

The deployment emits everything the fork bootstrap (`scripts/setup.ps1`) needs
to populate GitHub secrets/variables, without re-querying Azure:

`resourceGroup`, `location`, `appServiceName`, `appServiceUrl`,
`staticWebAppName`, `swaUrl`, `sqlServerName`, `sqlServerFqdn`,
`sqlDatabaseName`, `signalrName`, `signalrHostName`, the resolved per-resource
regions (`sqlServerLocation` / `appServiceLocation` / `signalrLocation` /
`staticWebAppLocation`), and the applied `entraTenantId` / `entraClientId` /
`entraAudience` / `dashboardCorsOrigin`.

## What's NOT provisioned here

- **Entra ID app registrations** — the app registrations themselves are created in the Azure AD / Entra portal (or by `scripts/setup.ps1`), not via resource deployment. Their IDs are *passed into* this template (`entraTenantId` / `entraClientId`) and applied as App Service settings.
- **Custom domains** — add later if you want `anchor.yourschool.be` instead of the auto-generated Azure URLs.
- **GitHub Actions deployment** — configure after the backend and dashboard projects exist.
