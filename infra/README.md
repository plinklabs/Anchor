# Anchor — Azure Infrastructure

All resources live in a single resource group in **West Europe**. Everything starts on free tiers; upgrade SignalR to Standard when you test with a real class (20+ students).

## Option A: Deploy with Bicep (recommended)

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
environment from parameters alone (no `arcadia` assumptions). The defaults
reproduce the live `anchor-rg` deployment, so the original environment still
deploys with no extra arguments.

| Parameter | Default | Purpose |
|---|---|---|
| `uniqueSuffix` | `arcadia` | Suffix for globally-unique names; drives the resource-name defaults below. |
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

## Option B: Manual setup via Azure Portal

If the CLI gives you trouble (TPM errors, etc.), create each resource manually in the portal. Everything goes into one resource group.

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
`sqlDatabaseName`, `signalrName`, `signalrHostName`, and the applied
`entraTenantId` / `entraClientId` / `entraAudience` / `dashboardCorsOrigin`.

## What's NOT provisioned here

- **Entra ID app registrations** — the app registrations themselves are created in the Azure AD / Entra portal (or by `scripts/setup.ps1`), not via resource deployment. Their IDs are *passed into* this template (`entraTenantId` / `entraClientId`) and applied as App Service settings.
- **Custom domains** — add later if you want `anchor.yourschool.be` instead of the auto-generated Azure URLs.
- **GitHub Actions deployment** — configure after the backend and dashboard projects exist.
