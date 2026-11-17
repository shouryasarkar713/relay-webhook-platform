@description('Deployment location for Managed Identities')
param location string

@description('Prefix for identity names')
param prefix string

@description('Resource tags')
param tags object = {}

resource apiIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${prefix}-api'
  location: location
  tags: tags
}

resource workerIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${prefix}-worker'
  location: location
  tags: tags
}

resource migrationIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${prefix}-migration'
  location: location
  tags: tags
}

output apiIdentityId string = apiIdentity.id
output apiIdentityClientId string = apiIdentity.properties.clientId
output apiIdentityPrincipalId string = apiIdentity.properties.principalId

output workerIdentityId string = workerIdentity.id
output workerIdentityClientId string = workerIdentity.properties.clientId
output workerIdentityPrincipalId string = workerIdentity.properties.principalId

output migrationIdentityId string = migrationIdentity.id
output migrationIdentityClientId string = migrationIdentity.properties.clientId
output migrationIdentityPrincipalId string = migrationIdentity.properties.principalId
