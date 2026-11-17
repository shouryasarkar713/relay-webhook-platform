@description('Deployment location for Container Apps')
param location string

@description('Container Apps Environment resource ID')
param environmentId string

@description('API Image tag (e.g. acrlogin/relay-api:commit-sha)')
param apiImage string

@description('Worker Image tag (e.g. acrlogin/relay-worker:commit-sha)')
param workerImage string

@description('ACR Login Server')
param acrLoginServer string

@description('API Managed Identity resource ID')
param apiIdentityId string

@description('API Managed Identity Client ID')
param apiIdentityClientId string

@description('Worker Managed Identity resource ID')
param workerIdentityId string

@description('Worker Managed Identity Client ID')
param workerIdentityClientId string

@description('Azure SQL Server FQDN')
param sqlServerFqdn string

@description('Azure SQL Database Name')
param sqlDatabaseName string

@description('Data Protection Blob URI')
param dataProtectionBlobUri string

@description('Data Protection Key Vault Key URI')
param dataProtectionKeyUri string

@description('Application Insights Connection String')
param appInsightsConnectionString string

@description('Service Bus Fully Qualified Namespace')
param serviceBusFqdn string

@description('Service Bus Queue Name')
param serviceBusQueueName string = 'relay-events'

@description('Frontend Web Endpoint for CORS')
param frontendWebEndpoint string

@description('API Container App CPU cores')
param apiCpu string = '0.5'

@description('API Container App Memory')
param apiMemory string = '1.0Gi'

@description('Worker Container App CPU cores')
param workerCpu string = '0.5'

@description('Worker Container App Memory')
param workerMemory string = '1.0Gi'

@description('Resource tags')
param tags object = {}

var sqlConnectionString = 'Server=tcp:${sqlServerFqdn},1433;Database=${sqlDatabaseName};Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;'

// 1. Relay.Api Container App
resource apiApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'ca-relay-api'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${apiIdentityId}': {}
    }
  }
  properties: {
    managedEnvironmentId: environmentId
    configuration: {
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
      }
      registries: [
        {
          server: acrLoginServer
          identity: apiIdentityId
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'relay-api'
          image: apiImage
          resources: {
            cpu: json(apiCpu)
            memory: apiMemory
          }
          env: [
            {
              name: 'ASPNETCORE_ENVIRONMENT'
              value: 'Production'
            }
            {
              name: 'AZURE_CLIENT_ID'
              value: apiIdentityClientId
            }
            {
              name: 'ConnectionStrings__RelayDatabase'
              value: sqlConnectionString
            }
            {
              name: 'DataProtection__BlobUri'
              value: dataProtectionBlobUri
            }
            {
              name: 'DataProtection__KeyVaultKeyIdentifier'
              value: dataProtectionKeyUri
            }
            {
              name: 'DataProtection__ApplicationName'
              value: 'Relay.Platform'
            }
            {
              name: 'Cors__AllowedOrigins__0'
              value: frontendWebEndpoint
            }
            {
              name: 'Cors__AllowedOrigins__1'
              value: 'http://localhost:5173'
            }
            {
              name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
              value: appInsightsConnectionString
            }
          ]
          probes: [
            {
              type: 'Liveness'
              httpGet: {
                path: '/health/live'
                port: 8080
              }
              initialDelaySeconds: 5
              periodSeconds: 15
              failureThreshold: 3
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/health/ready'
                port: 8080
              }
              initialDelaySeconds: 10
              periodSeconds: 15
              failureThreshold: 3
            }
          ]
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 3
        rules: [
          {
            name: 'http-scaling'
            http: {
              metadata: {
                concurrentRequests: '100'
              }
            }
          }
        ]
      }
    }
  }
}

// 2. Relay.Worker Container App (No public ingress)
resource workerApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'ca-relay-worker'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${workerIdentityId}': {}
    }
  }
  properties: {
    managedEnvironmentId: environmentId
    configuration: {
      registries: [
        {
          server: acrLoginServer
          identity: workerIdentityId
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'relay-worker'
          image: workerImage
          resources: {
            cpu: json(workerCpu)
            memory: workerMemory
          }
          env: [
            {
              name: 'DOTNET_ENVIRONMENT'
              value: 'Production'
            }
            {
              name: 'AZURE_CLIENT_ID'
              value: workerIdentityClientId
            }
            {
              name: 'ConnectionStrings__RelayDatabase'
              value: sqlConnectionString
            }
            {
              name: 'ServiceBus__FullyQualifiedNamespace'
              value: serviceBusFqdn
            }
            {
              name: 'ServiceBus__QueueOrTopicName'
              value: serviceBusQueueName
            }
            {
              name: 'DataProtection__BlobUri'
              value: dataProtectionBlobUri
            }
            {
              name: 'DataProtection__KeyVaultKeyIdentifier'
              value: dataProtectionKeyUri
            }
            {
              name: 'DataProtection__ApplicationName'
              value: 'Relay.Platform'
            }
            {
              name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
              value: appInsightsConnectionString
            }
          ]
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 3
      }
    }
  }
}

output apiAppId string = apiApp.id
output apiAppFqdn string = apiApp.properties.configuration.ingress.fqdn
output apiUrl string = 'https://${apiApp.properties.configuration.ingress.fqdn}'
output workerAppId string = workerApp.id
