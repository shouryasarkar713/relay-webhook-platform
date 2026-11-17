targetScope = 'resourceGroup'

@description('Deployment environment name (e.g. dev, demo, prod)')
@allowed([
  'dev'
  'demo'
  'prod'
])
param environment string = 'demo'

@description('Primary Azure region for all deployed resources')
param location string = resourceGroup().location

@description('Optional deployment salt/suffix to generate new globally unique resource names upon redeployment (avoids soft-deleted Key Vault naming collisions)')
param deploymentSalt string = ''

@description('Unique suffix for globally unique resources (defaults to uniqueString of resourceGroup id and optional deployment salt)')
param uniqueSuffix string = uniqueString(resourceGroup().id, deploymentSalt)

@description('Entra ID Administrator Display Name / Email for Azure SQL')
param entraAdminName string

@description('Entra ID Administrator Object ID (User/Group/App) for Azure SQL')
param entraAdminObjectId string

@description('Entra ID Administrator Type for Azure SQL')
@allowed([
  'User'
  'Group'
  'Application'
])
param entraAdminType string = 'User'

@description('Optional Object ID of the operator/deployer for data-plane Blob upload on frontend static storage')
param operatorPrincipalId string = entraAdminObjectId

@description('Docker image for Relay.Api (default: placeholder image before first CI build)')
param apiImage string = 'mcr.microsoft.com/azuredocs/aci-helloworld:latest'

@description('Docker image for Relay.Worker (default: placeholder image before first CI build)')
param workerImage string = 'mcr.microsoft.com/azuredocs/aci-helloworld:latest'

@description('Docker image for Relay.Admin CLI (default: placeholder image before first CI build)')
param adminImage string = 'mcr.microsoft.com/azuredocs/aci-helloworld:latest'

@description('Azure SQL Database SKU tier')
param sqlDatabaseSkuName string = 'Basic'

@description('Azure SQL Database Tier')
param sqlDatabaseSkuTier string = 'Basic'

var prefix = 'relay-${environment}'
var cleanSuffix = toLower(replace(uniqueSuffix, '-', ''))

var logWorkspaceName = 'log-${prefix}'
var appInsightsName = 'appi-${prefix}'
var caeName = 'cae-${prefix}'
var acrName = 'acrrelay${take(cleanSuffix, 12)}'
var storageKeysName = 'strelaykeys${take(cleanSuffix, 10)}'
var storageWebName = 'strelayweb${take(cleanSuffix, 11)}'
var keyVaultName = 'kv-relay-${take(cleanSuffix, 10)}'
var sqlServerName = 'sql-relay-${take(cleanSuffix, 12)}'
var sqlDbName = 'sqldb-${prefix}'
var serviceBusNamespaceName = 'sb-relay-${take(cleanSuffix, 12)}'

var commonTags = {
  Project: 'Relay'
  Environment: environment
  ManagedBy: 'Bicep'
}

// 1. Monitoring: Log Analytics Workspace & Application Insights
module monitoring 'modules/monitoring.bicep' = {
  name: 'monitoring-deployment'
  params: {
    location: location
    logAnalyticsWorkspaceName: logWorkspaceName
    applicationInsightsName: appInsightsName
    tags: commonTags
  }
}

// 2. User-Assigned Managed Identities (Least privilege separation)
module identities 'modules/identities.bicep' = {
  name: 'identities-deployment'
  params: {
    location: location
    prefix: prefix
    tags: commonTags
  }
}

// 3. Azure Container Registry
module containerRegistry 'modules/container-registry.bicep' = {
  name: 'acr-deployment'
  params: {
    location: location
    registryName: acrName
    sku: 'Basic'
    tags: commonTags
  }
}

// 4. Azure Container Apps Environment
module containerAppsEnvironment 'modules/container-apps-environment.bicep' = {
  name: 'cae-deployment'
  params: {
    location: location
    environmentName: caeName
    logAnalyticsWorkspaceId: monitoring.outputs.logAnalyticsWorkspaceId
    tags: commonTags
  }
}

// 5. Azure Blob Storage (Data Protection Key Ring)
module storage 'modules/storage.bicep' = {
  name: 'storage-keys-deployment'
  params: {
    location: location
    storageAccountName: storageKeysName
    tags: commonTags
  }
}

// 6. Azure Key Vault (Data Protection Master Key Encryption)
module keyVault 'modules/key-vault.bicep' = {
  name: 'keyvault-deployment'
  params: {
    location: location
    keyVaultName: keyVaultName
    tags: commonTags
  }
}

// 7. Azure SQL Database (Entra Passwordless Authentication)
module sqlDatabase 'modules/sql-database.bicep' = {
  name: 'sqldb-deployment'
  params: {
    location: location
    serverName: sqlServerName
    databaseName: sqlDbName
    entraAdminName: entraAdminName
    entraAdminObjectId: entraAdminObjectId
    entraAdminType: entraAdminType
    databaseSkuName: sqlDatabaseSkuName
    databaseSkuTier: sqlDatabaseSkuTier
    allowAzureIps: true
    tags: commonTags
  }
}

// 8. Azure Service Bus Standard (Duplicate Detection & Message Deduplication)
module serviceBus 'modules/service-bus.bicep' = {
  name: 'servicebus-deployment'
  params: {
    location: location
    namespaceName: serviceBusNamespaceName
    queueName: 'relay-events'
    tags: commonTags
  }
}

// 9. Frontend Static Web Hosting (Azure Storage $web)
module frontendHosting 'modules/frontend-hosting.bicep' = {
  name: 'frontend-deployment'
  params: {
    location: location
    storageAccountName: storageWebName
    tags: commonTags
  }
}

// 10. Role Assignments (Least-privilege RBAC)
module roleAssignments 'modules/role-assignments.bicep' = {
  name: 'rbac-deployment'
  params: {
    apiPrincipalId: identities.outputs.apiIdentityPrincipalId
    workerPrincipalId: identities.outputs.workerIdentityPrincipalId
    migrationPrincipalId: identities.outputs.migrationIdentityPrincipalId
    acrName: containerRegistry.outputs.registryName
    keysStorageAccountName: storage.outputs.storageAccountName
    keyVaultName: keyVault.outputs.keyVaultName
    serviceBusNamespaceName: serviceBus.outputs.namespaceName
    frontendStorageAccountName: frontendHosting.outputs.storageAccountName
    operatorPrincipalId: operatorPrincipalId
  }
}

// 11. Container Apps: Relay.Api and Relay.Worker
module containerApps 'modules/container-apps.bicep' = {
  name: 'containerapps-deployment'
  dependsOn: [
    roleAssignments
  ]
  params: {
    location: location
    environmentId: containerAppsEnvironment.outputs.environmentId
    apiImage: apiImage
    workerImage: workerImage
    acrLoginServer: containerRegistry.outputs.loginServer
    apiIdentityId: identities.outputs.apiIdentityId
    apiIdentityClientId: identities.outputs.apiIdentityClientId
    workerIdentityId: identities.outputs.workerIdentityId
    workerIdentityClientId: identities.outputs.workerIdentityClientId
    sqlServerFqdn: sqlDatabase.outputs.sqlServerFqdn
    sqlDatabaseName: sqlDatabase.outputs.databaseName
    dataProtectionBlobUri: storage.outputs.dataProtectionBlobUri
    dataProtectionKeyUri: keyVault.outputs.dataProtectionKeyUri
    appInsightsConnectionString: monitoring.outputs.applicationInsightsConnectionString
    serviceBusFqdn: serviceBus.outputs.fullyQualifiedNamespace
    serviceBusQueueName: serviceBus.outputs.queueName
    frontendWebEndpoint: frontendHosting.outputs.webEndpoint
    tags: commonTags
  }
}

// 12. Container Apps Job: Controlled Database Migration
module migrationJob 'modules/migration-job.bicep' = {
  name: 'migrationjob-deployment'
  dependsOn: [
    roleAssignments
  ]
  params: {
    location: location
    environmentId: containerAppsEnvironment.outputs.environmentId
    migrationImage: adminImage
    acrLoginServer: containerRegistry.outputs.loginServer
    migrationIdentityId: identities.outputs.migrationIdentityId
    migrationIdentityClientId: identities.outputs.migrationIdentityClientId
    sqlServerFqdn: sqlDatabase.outputs.sqlServerFqdn
    sqlDatabaseName: sqlDatabase.outputs.databaseName
    tags: commonTags
  }
}

// Deployment Outputs
output apiUrl string = containerApps.outputs.apiUrl
output apiFqdn string = containerApps.outputs.apiAppFqdn
output workerAppId string = containerApps.outputs.workerAppId
output migrationJobName string = migrationJob.outputs.migrationJobName
output sqlServerFqdn string = sqlDatabase.outputs.sqlServerFqdn
output sqlDatabaseName string = sqlDatabase.outputs.databaseName
output serviceBusFqdn string = serviceBus.outputs.fullyQualifiedNamespace
output frontendWebEndpoint string = frontendHosting.outputs.webEndpoint
output frontendStorageAccountName string = frontendHosting.outputs.storageAccountName
output acrName string = containerRegistry.outputs.registryName
output acrLoginServer string = containerRegistry.outputs.loginServer
output keyVaultName string = keyVault.outputs.keyVaultName
output keyVaultUri string = keyVault.outputs.keyVaultUri
output applicationInsightsId string = monitoring.outputs.applicationInsightsId
output apiIdentityClientId string = identities.outputs.apiIdentityClientId
output workerIdentityClientId string = identities.outputs.workerIdentityClientId
output migrationIdentityClientId string = identities.outputs.migrationIdentityClientId
