// Accountix .NET host on Azure App Service (Linux), in the same region as Neon.
// Documentation only until the go-ahead: nothing has been deployed.
//
//   az deployment group create -g accountix-prod -f dotnet/deploy/azure/main.bicep \
//     -p appName=accountix-net location=eastus2 publicHost=erp.example.com
//
// Then put the secrets in the Key Vault (docs/DEPLOY.md, "Secrets"). The app
// reads them through Key Vault references; no secret is in this file.

@description('Azure region: the one nearest the Neon project (docs/DEPLOY.md, "Region").')
param location string = resourceGroup().location

@description('Web app name: <appName>.azurewebsites.net')
param appName string = 'accountix-net'

@description('Plan size. P0v3 = 1 vCPU / 4 GB, the smallest tier with deployment slots and autoscale.')
@allowed(['P0v3', 'P1v3', 'P2v3', 'P1mv3'])
param sku string = 'P0v3'

@description('Instances to start with (2 or more keeps the app up while one restarts).')
@minValue(1)
param instances int = 2

@description('Most instances autoscale may add.')
param maxInstances int = 4

@description('The public domain both apps answer on (X-Forwarded-Host allowed).')
param publicHost string

@description('How many proxies in front append X-Forwarded-For: 1 = App Service only; 2 = Vercel rewrite or Front Door, then App Service.')
param forwardedLimit int = 2

@description('Key Vault name (globally unique).')
param keyVaultName string = '${appName}-kv'

var tags = { app: 'accountix', part: 'dotnet' }
var kvRef = '@Microsoft.KeyVault(VaultName=${keyVaultName};SecretName='

// Every setting is an environment variable, read the way the Python app reads
// its Vercel settings. Slot-specific ones are listed in slotConfigNames below.
var appSettings = [
  { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
  { name: 'SECRET_KEY', value: '${kvRef}SECRET-KEY)' }               // = the Python app's SECRET_KEY on Vercel
  { name: 'DATABASE_URL', value: '${kvRef}DATABASE-URL)' }           // Neon pooled endpoint (-pooler), sslmode=require
  { name: 'SENTRY_DSN', value: '${kvRef}SENTRY-DSN)' }               // empty secret = no error reports
  { name: 'SENTRY_ENVIRONMENT', value: 'production' }
  { name: 'DATA_PROTECTION_STORE', value: 'postgres' }                // shared key ring (Alembic 0017)
  { name: 'DB_MAX_POOL_SIZE', value: '20' }
  { name: 'DB_LOCK_TIMEOUT_MS', value: '15000' }
  { name: 'FORWARDED_LIMIT', value: string(forwardedLimit) }
  { name: 'FORWARDED_ALLOWED_HOSTS', value: '${publicHost},${appName}.azurewebsites.net' }
  { name: 'HSTS_MAX_AGE_DAYS', value: '365' }
  { name: 'LOG_FORMAT', value: 'json' }
  { name: 'WEBSITE_TIME_ZONE', value: 'UTC' }                         // the database holds naive UTC
  { name: 'WEBSITE_RUN_FROM_PACKAGE', value: '1' }                    // zip deploy mounts read-only, swaps atomically
  { name: 'WEBSITE_SWAP_WARMUP_PING_PATH', value: '/net/health' }     // a swap waits until new instances answer
  { name: 'WEBSITE_SWAP_WARMUP_PING_STATUSES', value: '200' }
]

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: '${appName}-plan'
  location: location
  tags: tags
  kind: 'linux'
  sku: { name: sku, capacity: instances }
  properties: { reserved: true, zoneRedundant: false }
}

var siteConfig = {
  linuxFxVersion: 'DOTNETCORE|10.0'
  appCommandLine: 'dotnet Accountix.Web.dll'
  alwaysOn: true
  healthCheckPath: '/net/health'
  http20Enabled: true
  minTlsVersion: '1.2'
  ftpsState: 'Disabled'
  use32BitWorkerProcess: false
  webSocketsEnabled: false
  appSettings: appSettings
}

resource site 'Microsoft.Web/sites@2023-12-01' = {
  name: appName
  location: location
  tags: tags
  kind: 'app,linux'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    // The key ring is shared (Postgres), so any instance can answer any
    // request: no ARR affinity cookie, even load spread.
    clientAffinityEnabled: false
    siteConfig: siteConfig
  }
}

// Deploy here, warm up, then swap: no cold instance takes traffic, and the
// swap back is the rollback (docs/CUTOVER.md).
resource staging 'Microsoft.Web/sites/slots@2023-12-01' = {
  parent: site
  name: 'staging'
  location: location
  tags: tags
  kind: 'app,linux'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    clientAffinityEnabled: false
    siteConfig: union(siteConfig, {
      // A staging slot that is not taking traffic need not keep the database awake.
      alwaysOn: false
      appSettings: concat(appSettings, [ { name: 'SENTRY_ENVIRONMENT', value: 'staging' } ])
    })
  }
}

resource slotNames 'Microsoft.Web/sites/config@2023-12-01' = {
  parent: site
  name: 'slotConfigNames'
  properties: { appSettingNames: ['SENTRY_ENVIRONMENT'] }
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 30
    enablePurgeProtection: true
  }
}

// Key Vault Secrets User for the app and its slot (read secrets, nothing else).
var secretsUser = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
resource siteCanRead 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: vault
  name: guid(vault.id, site.id, secretsUser)
  properties: { roleDefinitionId: secretsUser, principalId: site.identity.principalId, principalType: 'ServicePrincipal' }
}
resource slotCanRead 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: vault
  name: guid(vault.id, staging.id, secretsUser)
  properties: { roleDefinitionId: secretsUser, principalId: staging.identity.principalId, principalType: 'ServicePrincipal' }
}

// Logs: stdout (one JSON object per request) and the platform's HTTP log.
resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${appName}-logs'
  location: location
  tags: tags
  properties: { sku: { name: 'PerGB2018' }, retentionInDays: 30 }
}
resource diag 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  scope: site
  name: 'to-log-analytics'
  properties: {
    workspaceId: logs.id
    logs: [
      { category: 'AppServiceConsoleLogs', enabled: true }
      { category: 'AppServiceHTTPLogs', enabled: true }
      { category: 'AppServicePlatformLogs', enabled: true }
    ]
    metrics: [ { category: 'AllMetrics', enabled: true } ]
  }
}

// Scale out on CPU; scale in slowly. Each instance holds at most
// DB_MAX_POOL_SIZE connections, so maxInstances x 20 must fit Neon's pooler.
resource autoscale 'Microsoft.Insights/autoscalesettings@2022-10-01' = {
  name: '${appName}-autoscale'
  location: location
  tags: tags
  properties: {
    enabled: true
    targetResourceUri: plan.id
    profiles: [
      {
        name: 'cpu'
        capacity: { minimum: string(instances), maximum: string(maxInstances), default: string(instances) }
        rules: [
          {
            metricTrigger: {
              metricName: 'CpuPercentage', metricResourceUri: plan.id, operator: 'GreaterThan', threshold: 70
              statistic: 'Average', timeAggregation: 'Average', timeGrain: 'PT1M', timeWindow: 'PT5M'
            }
            scaleAction: { direction: 'Increase', type: 'ChangeCount', value: '1', cooldown: 'PT5M' }
          }
          {
            metricTrigger: {
              metricName: 'CpuPercentage', metricResourceUri: plan.id, operator: 'LessThan', threshold: 30
              statistic: 'Average', timeAggregation: 'Average', timeGrain: 'PT1M', timeWindow: 'PT15M'
            }
            scaleAction: { direction: 'Decrease', type: 'ChangeCount', value: '1', cooldown: 'PT15M' }
          }
        ]
      }
    ]
  }
}

output defaultHost string = site.properties.defaultHostName
output stagingHost string = staging.properties.defaultHostName
output outboundIps string = site.properties.outboundIpAddresses   // for Neon's IP allow list
output principalId string = site.identity.principalId
