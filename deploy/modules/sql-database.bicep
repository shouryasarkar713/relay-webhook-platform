@description('Deployment location for Azure SQL resources')
param location string

@description('Azure SQL logical server name')
param serverName string

@description('Azure SQL database name')
param databaseName string

@description('Entra Admin Display Name / Object ID for SQL Server')
param entraAdminName string

@description('Entra Admin Object ID (User, Group, or Managed Identity)')
param entraAdminObjectId string

@description('Entra Admin Type (User, Group, or Application)')
@allowed([
  'User'
  'Group'
  'Application'
])
param entraAdminType string = 'User'

@description('Database SKU name (e.g. Basic, S0, GP_S_Gen5_1)')
param databaseSkuName string = 'Basic'

@description('Database SKU tier (e.g. Basic, Standard, GeneralPurpose)')
param databaseSkuTier string = 'Basic'

@description('Database max size in bytes')
param maxSizeBytes int = 2147483648 // 2 GB for Basic

@description('Allow Azure internal services to connect through firewall')
param allowAzureIps bool = true

@description('Resource tags')
param tags object = {}

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: serverName
  location: location
  tags: tags
  properties: {
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      principalType: entraAdminType
      login: entraAdminName
      sid: entraAdminObjectId
      tenantId: subscription().tenantId
      azureADOnlyAuthentication: true
    }
  }
}

resource allowAzureFirewallRule 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = if (allowAzureIps) {
  parent: sqlServer
  name: 'AllowAllWindowsAzureIps'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource sqlDatabase 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: databaseName
  location: location
  tags: tags
  sku: {
    name: databaseSkuName
    tier: databaseSkuTier
  }
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
    maxSizeBytes: maxSizeBytes
    zoneRedundant: false
    readScale: 'Disabled'
  }
}

output sqlServerId string = sqlServer.id
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output databaseName string = sqlDatabase.name
