// ──────────────────────────────────────────────
// Anchor — Azure infrastructure
// Deploy:  az deployment group create \
//            --resource-group anchor-rg \
//            --template-file infra/main.bicep \
//            --parameters sqlAdminPassword='<your-password>'
// ──────────────────────────────────────────────

@description('Azure region for all resources.')
param location string = 'westeurope'

@description('Unique suffix appended to globally-unique resource names.')
param uniqueSuffix string = uniqueString(resourceGroup().id)

@description('SQL admin username.')
param sqlAdminLogin string = 'anchoradmin'

@secure()
@description('SQL admin password.')
param sqlAdminPassword string

// ── SQL Server ──────────────────────────────

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: 'anchor-sql-${uniqueSuffix}'
  location: location
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
  name: 'anchordb'
  location: location
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
  name: 'anchor-plan'
  location: location
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
  name: 'anchor-api-${uniqueSuffix}'
  location: location
  properties: {
    serverFarmId: appPlan.id
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|8.0'
      appSettings: [
        {
          name: 'Azure__SignalR__ConnectionString'
          value: signalr.listKeys().primaryConnectionString
        }
      ]
      connectionStrings: [
        {
          name: 'DefaultConnection'
          connectionString: 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=anchordb;User ID=${sqlAdminLogin};Password=${sqlAdminPassword};Encrypt=true;TrustServerCertificate=false;'
          type: 'SQLAzure'
        }
      ]
    }
  }
}

// ── SignalR Service (Free) ──────────────────

resource signalr 'Microsoft.SignalRService/signalR@2024-03-01' = {
  name: 'anchor-signalr-${uniqueSuffix}'
  location: location
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
  name: 'anchor-dashboard-${uniqueSuffix}'
  location: location
  sku: {
    name: 'Free'
    tier: 'Free'
  }
  properties: {}
}

// ── Outputs ─────────────────────────────────

output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output appServiceUrl string = 'https://${appService.properties.defaultHostName}'
output signalrHostName string = signalr.properties.hostName
output swaUrl string = 'https://${swa.properties.defaultHostname}'
output resourceGroup string = resourceGroup().name
