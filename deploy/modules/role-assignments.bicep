@description('Principal ID of the API Managed Identity')
param apiPrincipalId string

@description('Principal ID of the Worker Managed Identity')
param workerPrincipalId string

@description('Principal ID of the Migration Managed Identity')
param migrationPrincipalId string

@description('ACR Resource Name')
param acrName string

@description('Keys Storage Account Name')
param keysStorageAccountName string

@description('Key Vault Name')
param keyVaultName string

@description('Service Bus Namespace Name')
param serviceBusNamespaceName string

@description('Frontend Storage Account Name (optional)')
param frontendStorageAccountName string = ''

@description('Principal ID of the Operator / Deployer (optional, for frontend static web upload permissions)')
param operatorPrincipalId string = ''

// Built-in Azure RBAC Role Definition IDs
var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d'
var storageBlobDataContributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
var keyVaultCryptoUserRoleId = '12338053-0c79-4a76-8571-2d04e209677b'
var serviceBusDataSenderRoleId = '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39'
var serviceBusDataReceiverRoleId = '4f6d3a01-b98a-4e4f-9f77-51e389e77357'

// Existing resource references for scoped role assignments
resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: acrName
}

resource keysStorage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: keysStorageAccountName
}

resource frontendStorage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = if (!empty(frontendStorageAccountName)) {
  name: frontendStorageAccountName
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

resource serviceBus 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' existing = {
  name: serviceBusNamespaceName
}

// 1. ACR Pull for API, Worker & Migration Job
resource apiAcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: acr
  name: guid(acr.id, apiPrincipalId, acrPullRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
    principalId: apiPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource workerAcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: acr
  name: guid(acr.id, workerPrincipalId, acrPullRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
    principalId: workerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource migrationAcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(migrationPrincipalId)) {
  scope: acr
  name: guid(acr.id, migrationPrincipalId, acrPullRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
    principalId: migrationPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// 2. Storage Blob Data Contributor on Data Protection key container for API & Worker
resource apiBlobContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keysStorage
  name: guid(keysStorage.id, apiPrincipalId, storageBlobDataContributorRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributorRoleId)
    principalId: apiPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource workerBlobContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keysStorage
  name: guid(keysStorage.id, workerPrincipalId, storageBlobDataContributorRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributorRoleId)
    principalId: workerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// 3. Storage Blob Data Contributor on Frontend static storage for Operator upload (avoids upload-batch 403)
resource operatorFrontendBlobContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(operatorPrincipalId) && !empty(frontendStorageAccountName)) {
  scope: frontendStorage
  name: guid(frontendStorage.id, operatorPrincipalId, storageBlobDataContributorRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributorRoleId)
    principalId: operatorPrincipalId
    principalType: 'User'
  }
}

// 4. Key Vault Crypto User on Key Vault for API & Worker (Data Protection key unwrap/wrap)
resource apiKeyVaultCrypto 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyVault
  name: guid(keyVault.id, apiPrincipalId, keyVaultCryptoUserRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultCryptoUserRoleId)
    principalId: apiPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource workerKeyVaultCrypto 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyVault
  name: guid(keyVault.id, workerPrincipalId, keyVaultCryptoUserRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultCryptoUserRoleId)
    principalId: workerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// 5. Azure Service Bus Sender & Receiver for Worker ONLY (Least privilege - API has no access)
resource workerServiceBusSender 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: serviceBus
  name: guid(serviceBus.id, workerPrincipalId, serviceBusDataSenderRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', serviceBusDataSenderRoleId)
    principalId: workerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource workerServiceBusReceiver 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: serviceBus
  name: guid(serviceBus.id, workerPrincipalId, serviceBusDataReceiverRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', serviceBusDataReceiverRoleId)
    principalId: workerPrincipalId
    principalType: 'ServicePrincipal'
  }
}
