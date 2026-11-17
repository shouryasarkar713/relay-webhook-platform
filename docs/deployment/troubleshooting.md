# Relay — Cloud Troubleshooting Guide

## 1. Overview

This guide details common deployment and runtime failure modes in Microsoft Azure, diagnostic commands, and remediation steps.

---

## 2. Common Deployment Issues

### 2.1 Bicep Name Collision on Globally Unique Resources
- **Symptom**: `StorageAccountAlreadyExists` or `VaultAlreadyExists`.
- **Cause**: Azure Storage Account names (`strelaydp<env>`) and Key Vault names (`kv-relay-<env>`) must be globally unique across all Azure subscriptions worldwide.
- **Remediation**:
  - Update `environmentName` parameter in `deploy/parameters/dev-demo.bicepparam` to include a unique suffix (e.g., `demo-abc` or your initials `demoss`).

### 2.2 Principal Type Missing on Role Assignment
- **Symptom**: `RoleAssignmentExists` error or slow propagation during deployment.
- **Cause**: When creating role assignments for Managed Identities, Azure Entra ID replication may take several seconds.
- **Remediation**:
  - Ensure Bicep modules specify `principalType: 'ServicePrincipal'` on all role assignments (already implemented in `deploy/modules/role-assignments.bicep`).

### 2.3 Azure Resource Provider Not Registered
- **Symptom**: `SubscriptionNotRegistered` for `Microsoft.ContainerApp` or `Microsoft.KeyVault`.
- **Remediation**:
  - Run `az provider register --namespace <Namespace>` as detailed in [Azure Prerequisites](file:///home/shouryasarkar/Desktop/relay/docs/deployment/azure-prerequisites.md).

---

## 3. Container Runtime & Health Probe Failures

### 3.1 Container CrashLoopBackOff / Probe Failures
- **Symptom**: `ca-relay-api` or `ca-relay-worker` fails to start; Container App reports `Degraded` or `CrashLoopBackOff`.
- **Diagnosis**:
  1. Inspect container console logs:
     ```bash
     az containerapp logs show \
       --name ca-relay-api \
       --resource-group rg-relay-demo \
       --follow
     ```
  2. Query recent error events in Log Analytics:
     ```kusto
     ContainerAppSystemLogs_CL
     | where ContainerAppName_s == "ca-relay-api"
     | where Reason_s has "Failed" or Reason_s has "Probe"
     | order by TimeGenerated desc
     | take 20
     ```
- **Common Causes**:
  - **Missing Environment Variables**: Ensure `ConnectionStrings__RelayDatabase`, `DataProtection__BlobUri`, and `DataProtection__KeyVaultKeyIdentifier` are populated.
  - **Database Not Reached**: Ensure database schema migrations were executed via `relay-admin migrate` before container startup.

---

## 4. Identity & Authentication Errors

### 4.1 Azure SQL Database Token Failure
- **Symptom**: `Microsoft.Data.SqlClient.SqlException: Login failed for user '<token-identified principal>'`.
- **Cause**: The Managed Identity has not been granted database permissions inside SQL Server.
- **Remediation**:
  - Verify that the Managed Identity has been created as an external database user:
    ```sql
    -- Connect as Entra Admin to sqldb-relay:
    CREATE USER [id-relay-api] FROM EXTERNAL PROVIDER;
    ALTER ROLE db_datareader ADD MEMBER [id-relay-api];
    ALTER ROLE db_datawriter ADD MEMBER [id-relay-api];

    CREATE USER [id-relay-worker] FROM EXTERNAL PROVIDER;
    ALTER ROLE db_datareader ADD MEMBER [id-relay-worker];
    ALTER ROLE db_datawriter ADD MEMBER [id-relay-worker];
    ```

### 4.2 Key Vault or Storage Access Denied
- **Symptom**: `Azure.Identity.CredentialUnavailableException` or `403 Forbidden` when writing to Data Protection blob or decrypting keys.
- **Cause**: Managed Identity lacks `Key Vault Crypto User` or `Storage Blob Data Contributor` RBAC assignment.
- **Remediation**:
  - Verify role assignments in Azure Portal or via CLI:
    ```bash
    az role assignment list --assignee <IDENTITY_PRINCIPAL_ID> --all -o table
    ```

---

## 5. Webhook Delivery & Service Bus Issues

### 5.1 Service Bus Authorization Error
- **Symptom**: `UnauthorizedException: 401 SubCode=40100: Unauthorized access` on `relay-events` queue.
- **Cause**: `id-relay-worker` lacks `Azure Service Bus Data Receiver` or `Data Sender` role.
- **Remediation**:
  - Confirm role assignments for `id-relay-worker` on the Service Bus namespace.

### 5.2 SSRF Block on Webhook Deliveries
- **Symptom**: Webhook delivery fails immediately with `TerminalFailure` and `FailureReason: Webhook endpoint URL resolved to a restricted IP address`.
- **Cause**: The customer supplied an IP address within RFC 1918 private ranges (`10.x`, `192.168.x`, `172.16.x`) or loopback (`127.0.0.1`), or an Azure metadata IP (`169.254.169.254`).
- **Remediation**:
  - Inform customer to register a publicly routable HTTPS domain.

---

## 6. Log Inspection Queries (KQL)

Execute these queries in the **Azure Log Analytics Workspace**:

### 6.1 View Outbox Dispatcher Activity
```kusto
ContainerAppConsoleLogs_CL
| where ContainerAppName_s == "ca-relay-worker"
| where Log_s has "OutboxDispatcher"
| project TimeGenerated, Log_s
| order by TimeGenerated desc
| take 100
```

### 6.2 View Failed Delivery Attempts
```kusto
ContainerAppConsoleLogs_CL
| where ContainerAppName_s == "ca-relay-worker"
| where Log_s has "DeliveryWorker" and Log_s has "failed"
| project TimeGenerated, Log_s
| order by TimeGenerated desc
| take 50
```
