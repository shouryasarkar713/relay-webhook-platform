# Relay — Cloud Teardown & Resource Decommissioning

## 1. Overview

To prevent unintended cloud hosting charges after completing demonstrations, evaluations, or test cycles, all Relay Azure resources can be cleanly decommissioned.

> [!IMPORTANT]
> **Key Vault Soft-Delete Retention Invariant**:
> Normal teardown deletes the resource group, which places the Azure Key Vault into the standard Azure **soft-delete state** (retained for 90 days). The cryptographic master key `relay-dp-key` is **never automatically purged**. This guarantees that any existing database backups or exported Data Protection blobs can still be decrypted if the Key Vault is restored.
> **Never purge a Key Vault if you intend to recover or read previously encrypted webhook signing secrets.**

---

## 2. Automated Teardown via Script

Relay provides a guarded teardown script at [`deploy/scripts/teardown.sh`](file:///home/shouryasarkar/Desktop/relay/deploy/scripts/teardown.sh).

### Safety Features:
- **CI/CD Block**: Automatically aborts if running inside CI (`CI=true` or `GITHUB_ACTIONS=true`).
- **Resource Group Guard**: Only operates on resource groups matching the `rg-relay-*` pattern.
- **Dry-Run Inspection**: Supports `--dry-run` to inspect actions without modifying cloud state.
- **Mandatory Flag**: Requires the explicit confirmation flag `--confirm-delete-all-resources`.
- **Interactive Check**: Prompts the operator to type the exact resource group name before deleting.
- **Preserves Soft-Delete**: Leaves Key Vault soft-delete active (zero purging).

### Safe Dry-Run:
```bash
./deploy/scripts/teardown.sh demo --dry-run
```

### Execution:
```bash
./deploy/scripts/teardown.sh demo --confirm-delete-all-resources
```

---

## 3. Manual Teardown via Azure CLI

If preferred, teardown can be executed manually via Azure CLI commands:

### Delete Resource Group
Deleting the parent resource group automatically deletes all contained resources (Container Apps, Environment, Azure SQL Database, Service Bus, Storage Accounts, and Managed Identities):

```bash
az group delete \
  --name rg-relay-demo \
  --yes \
  --no-wait
```

The Azure Key Vault will enter the soft-delete state for 90 days. If you re-deploy Relay to the same subscription and region using the same Key Vault name before 90 days elapse, Azure allows recovering the soft-deleted vault:
```bash
# Recover soft-deleted vault if re-deploying with the same name:
az keyvault recover --name kv-relay-demo --location centralindia
```

---

## 4. Verification of Deletion

Verify that the resource group has been deleted:

```bash
az group exists --name rg-relay-demo
```
*Expected output: `false`.*

Confirm that no active billable compute or database resources remain in the subscription:
```bash
az resource list --query "[?contains(name, 'relay')].{Name:name, Type:type, ResourceGroup:resourceGroup}" -o table
```
