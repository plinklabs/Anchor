// ──────────────────────────────────────────────
// Anchor — Azure infrastructure
// Deploy:  az deployment group create \
//            --resource-group anchor-rg \
//            --template-file infra/main.bicep \
//            --parameters sqlAdminPassword='<your-password>' \
//                         entraTenantId='<tenant-guid>' \
//                         entraClientId='<api-client-guid>'
//
// Every name and identifier is a parameter so a fork can stand up its own
// environment from parameters alone (issue #212). Resource *names* default to
// the live `anchor-rg` deployment (uniqueSuffix=arcadia) so the original
// environment still deploys with no extra arguments. Regions, however, are NOT
// hardcoded: `location` defaults to the resource group's region and each
// resource can override it (the live arcadia env is itself split across two
// regions — App Service / plan / SQL in Belgium Central, SignalR / Static Web
// App in West Europe — which a single location cannot reproduce).
// ──────────────────────────────────────────────

@description('Default Azure region for all resources. Defaults to the resource group region; override per-resource with the *Location params below.')
param location string = resourceGroup().location

// ── Per-resource region overrides ───────────
// Each defaults to `location`. Override individually to reproduce a split
// layout (e.g. the live arcadia env) or to place a resource in a region where
// the others are not offered (Static Web Apps / SignalR have a limited region
// set). The setup script reads an existing resource's current region and pins
// it here on re-run, so adopting an environment never tries to move a resource
// (region is immutable in Azure).

@description('Region for the SQL logical server + database.')
param sqlServerLocation string = location

@description('Region for the App Service and its plan.')
param appServiceLocation string = location

@description('Region for the SignalR Service.')
param signalrLocation string = location

@description('Region for the Static Web App.')
param staticWebAppLocation string = location

@description('Suffix appended to globally-unique resource names. Defaults to "arcadia" to match the existing anchor-rg deployment; override to stand up a second environment.')
param uniqueSuffix string = 'arcadia'

// ── Resource names ──────────────────────────
// All resource names are parameters with suffix-derived defaults so a fork
// does not inherit the original `arcadia`/`anchor-*` names. Override any of
// them to match an existing manually-created resource.

@description('Name of the SQL logical server (globally unique). Defaults to anchor-sql-<suffix>.')
param sqlServerName string = 'anchor-sql-${uniqueSuffix}'

@description('Name of the SQL database.')
param sqlDatabaseName string = 'anchordb'

@description('Name of the App Service (backend API, globally unique). Defaults to anchor-api-<suffix>.')
param appServiceName string = 'anchor-api-${uniqueSuffix}'

@description('Name of the App Service Plan. Defaults to the auto-generated name of the original manually-created plan in anchor-rg; override for a fresh environment, e.g. asp-anchor-<suffix>.')
param appServicePlanName string = 'ASP-anchorrg-b49b'

@description('Name of the SignalR Service (globally unique). Defaults to anchor-signalr; override for additional environments, e.g. anchor-signalr-<suffix>.')
param signalrName string = 'anchor-signalr'

@description('Name of the Static Web App for the Flutter dashboard. Defaults to anchor-dashboard; override for additional environments, e.g. anchor-dashboard-<suffix>.')
param staticWebAppName string = 'anchor-dashboard'

// ── SQL admin ───────────────────────────────

@description('SQL admin username.')
param sqlAdminLogin string = 'anchoradmin'

@secure()
@description('SQL admin password.')
param sqlAdminPassword string

// ── Entra ID (Azure AD) ─────────────────────
// The deployed API reads these as App Service application settings
// (AzureAd__* double-underscore form → AzureAd:* config keys). They are
// environment-specific and are NOT committed to appsettings.json, so the
// deploy must supply them rather than relying on the repo (pairs with A1 /
// issue #201). See docs/RELEASE.md.

@description('Entra authority instance. Defaults to the current cloud login endpoint.')
param entraInstance string = environment().authentication.loginEndpoint

@description('Entra tenant ID (GUID) the API validates tokens against. Required for a real deploy; blank leaves the App Service setting empty so the API would reject all tokens.')
param entraTenantId string = ''

@description('Entra client ID (GUID) of the API app registration.')
param entraClientId string = ''

@description('JWT audience the API validates. Defaults to api://<entraClientId> when a client ID is supplied.')
param entraAudience string = empty(entraClientId) ? '' : 'api://${entraClientId}'

// ── CORS ────────────────────────────────────

@description('Allowed CORS origin for the dashboard SPA. Leave blank to default to the deployed Static Web App URL (so the dashboard can call the API from the browser); override to point at a custom domain. A parameter default cannot reference the SWA resource, so the empty-string fallback is resolved in the dashboardCorsOrigin variable below.')
param dashboardCorsOriginOverride string = ''

// ── Variables ───────────────────────────────

// Effective CORS origin: the explicit override when supplied, else the deployed
// SWA URL. Resolved here (not in the parameter default) because a parameter
// default cannot reference another resource (BCP072).
var dashboardCorsOrigin = empty(dashboardCorsOriginOverride)
  ? 'https://${swa.properties.defaultHostname}'
  : dashboardCorsOriginOverride

// ── SQL Server ──────────────────────────────

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: sqlServerName
  location: sqlServerLocation
  properties: {
    administratorLogin: sqlAdminLogin
    administratorLoginPassword: sqlAdminPassword
    version: '12.0'
  }
}

// Allow Azure services to reach the SQL server
resource sqlFirewallAllowAzure 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

// ── SQL Database (Serverless) ───────────────

resource sqlDb 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: sqlDatabaseName
  location: sqlServerLocation
  sku: {
    name: 'GP_S_Gen5'   // General Purpose, Serverless, Gen5
    tier: 'GeneralPurpose'
    family: 'Gen5'
    capacity: 2          // max vCores
  }
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
    autoPauseDelay: 60   // minutes idle before auto-pause
    minCapacity: json('0.5') // min vCores
    requestedBackupStorageRedundancy: 'Local'
  }
}

// ── App Service Plan (Linux, Free) ──────────

resource appPlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: appServicePlanName
  location: appServiceLocation
  kind: 'linux'
  properties: {
    reserved: true       // required for Linux
  }
  sku: {
    name: 'F1'
    tier: 'Free'
  }
}

// ── App Service (ASP.NET Core backend) ──────

resource appService 'Microsoft.Web/sites@2023-12-01' = {
  name: appServiceName
  location: appServiceLocation
  properties: {
    serverFarmId: appPlan.id
    siteConfig: {
      // Keep in sync with backend/src/Anchor.Api/Anchor.Api.csproj <TargetFramework>
      // and the `dotnet-version` in .github/workflows/backend-deploy.yml — a build
      // targeting a newer runtime than this deploys "successfully" but 503s on a
      // host pinned to the older one (#276).
      linuxFxVersion: 'DOTNETCORE|10.0'
      // Entra + CORS application settings (double-underscore form). Provisioning
      // them here means the deployed API gets its environment-specific config
      // from the infra, not from committed appsettings.json.
      appSettings: [
        {
          name: 'ASPNETCORE_ENVIRONMENT'
          value: 'Production'
        }
        {
          name: 'Azure__SignalR__ConnectionString'
          value: signalr.listKeys().primaryConnectionString
        }
        {
          name: 'AzureAd__Instance'
          value: entraInstance
        }
        {
          name: 'AzureAd__TenantId'
          value: entraTenantId
        }
        {
          name: 'AzureAd__ClientId'
          value: entraClientId
        }
        {
          name: 'AzureAd__Audience'
          value: entraAudience
        }
        {
          name: 'Cors__AllowedOrigins__0'
          value: dashboardCorsOrigin
        }
      ]
      connectionStrings: [
        {
          name: 'DefaultConnection'
          connectionString: 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${sqlDatabaseName};User ID=${sqlAdminLogin};Password=${sqlAdminPassword};Encrypt=true;TrustServerCertificate=false;'
          type: 'SQLAzure'
        }
      ]
    }
  }
}

// ── SignalR Service (Free) ──────────────────

resource signalr 'Microsoft.SignalRService/signalR@2024-03-01' = {
  name: signalrName
  location: signalrLocation
  sku: {
    name: 'Free_F1'
    capacity: 1
  }
  properties: {
    features: [
      {
        flag: 'ServiceMode'
        value: 'Default'
      }
    ]
  }
}

// ── Static Web App (Flutter dashboard) ──────

resource swa 'Microsoft.Web/staticSites@2023-12-01' = {
  name: staticWebAppName
  location: staticWebAppLocation
  sku: {
    name: 'Free'
    tier: 'Free'
  }
  properties: {}
}

// ── Outputs ─────────────────────────────────
// Everything scripts/setup.ps1 needs to populate GitHub secrets/variables and
// finish the fork bootstrap, without re-querying Azure for resource names.

output resourceGroup string = resourceGroup().name
output location string = location

// Resolved per-resource regions, so the setup script can record where each
// resource actually landed (and pin them on a subsequent adopt/re-run).
output sqlServerLocation string = sqlServerLocation
output appServiceLocation string = appServiceLocation
output signalrLocation string = signalrLocation
output staticWebAppLocation string = staticWebAppLocation

output appServiceName string = appService.name
output appServiceUrl string = 'https://${appService.properties.defaultHostName}'

output staticWebAppName string = swa.name
output swaUrl string = 'https://${swa.properties.defaultHostname}'

output sqlServerName string = sqlServer.name
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output sqlDatabaseName string = sqlDb.name

output signalrName string = signalr.name
output signalrHostName string = signalr.properties.hostName

// Echo back the Entra config the deploy applied, so the dashboard build
// variables (ENTRA_TENANT_ID / ENTRA_CLIENT_ID / API_SCOPE) and the operator
// checklist can be derived from one deployment output.
output entraTenantId string = entraTenantId
output entraClientId string = entraClientId
output entraAudience string = entraAudience
output dashboardCorsOrigin string = dashboardCorsOrigin
