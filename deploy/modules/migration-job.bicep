@description('Deployment location')
param location string

@description('Container Apps Environment resource ID')
param environmentId string

@description('Admin CLI container image')
param migrationImage string

@description('ACR login server')
param acrLoginServer string

@description('Migration Managed Identity resource ID')
param migrationIdentityId string

@description('Migration Managed Identity Client ID')
param migrationIdentityClientId string

@description('Azure SQL Server FQDN')
param sqlServerFqdn string

@description('Azure SQL Database Name')
param sqlDatabaseName string

@description('Resource tags')
param tags object = {}

var sqlConnectionString = 'Server=tcp:${sqlServerFqdn},1433;Database=${sqlDatabaseName};Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;'

resource migrationJob 'Microsoft.App/jobs@2024-03-01' = {
  name: 'job-relay-migration'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${migrationIdentityId}': {}
    }
  }
  properties: {
    environmentId: environmentId
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 300
      replicaRetryLimit: 1
      registries: [
        {
          server: acrLoginServer
          identity: migrationIdentityId
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'relay-migration'
          image: migrationImage
          command: [
            './relay-admin'
            'migrate'
          ]
          env: [
            {
              name: 'ConnectionStrings__RelayDatabase'
              value: sqlConnectionString
            }
            {
              name: 'AZURE_CLIENT_ID'
              value: migrationIdentityClientId
            }
          ]
          resources: {
            cpu: json('0.5')
            memory: '1.0Gi'
          }
        }
      ]
    }
  }
}

output migrationJobId string = migrationJob.id
output migrationJobName string = migrationJob.name
