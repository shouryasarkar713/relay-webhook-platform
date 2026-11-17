using '../main.bicep'

// =============================================================================
// Relay Demonstration Deployment Parameters
// Primary Region: Central India (Pune)
// Secondary/Backup Region: South India (Chennai)
// =============================================================================

param environment = 'demo'

// Primary Indian deployment region providing complete service availability across all 10 required providers:
// - Microsoft.App, Microsoft.Authorization, Microsoft.ContainerRegistry, Microsoft.Insights
// - Microsoft.KeyVault, Microsoft.ManagedIdentity, Microsoft.OperationalInsights
// - Microsoft.ServiceBus, Microsoft.Sql, Microsoft.Storage
param location = 'centralindia'

// Optional deployment salt: set to a short string (e.g. 'v2' or 'run2') when re-deploying
// into the same resource group to generate fresh globally unique resource names and avoid
// soft-deleted Key Vault naming collisions.
param deploymentSalt = ''

// Replace with target developer's Entra ID email/display name and Object ID before deployment
param entraAdminName = 'developer@example.com'
param entraAdminObjectId = '00000000-0000-0000-0000-000000000000'
param entraAdminType = 'User'

// Explicit SKU Selection: Basic Tier (5 DTU, 2 GB)
// Selected over General Purpose Serverless because Relay.Worker continuously polls SQL Server
// for Outbox and Delivery records, which prevents Serverless auto-pause (requires 0 active sessions).
// Basic DTU provides deterministic, always-online low cost (~$4.99/month, zero cold start latency).
param sqlDatabaseSkuName = 'Basic'
param sqlDatabaseSkuTier = 'Basic'
