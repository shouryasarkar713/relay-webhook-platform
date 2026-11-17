#!/usr/bin/env bash
set -euo pipefail

# ==============================================================================
# Relay Guarded Teardown Script
# ==============================================================================
# SAFETY INVARIANTS:
# 1. Never executes automatically in CI/CD environments.
# 2. Refuses to act on resource groups that do not match the 'rg-relay-*' pattern.
# 3. Requires explicit confirmation flags and interactive confirmation.
# 4. Supports --dry-run mode to inspect resources without modifying state.
# 5. NEVER automatically purges soft-deleted Key Vaults or cryptographic keys.
# ==============================================================================

ENV="${1:-demo}"
CONFIRM_FLAG="${2:-}"
EXTRA_FLAG="${3:-}"
RG_NAME="rg-relay-${ENV}"

# 1. Prevent execution in automated CI environments
if [ "${CI:-false}" = "true" ] || [ "${GITHUB_ACTIONS:-false}" = "true" ]; then
    echo "ERROR: Teardown script is strictly prohibited from running in automated CI/CD pipelines."
    exit 1
fi

# 2. Guard against arbitrary resource group deletion
if [[ ! "${RG_NAME}" =~ ^rg-relay-[a-zA-Z0-9-]+$ ]]; then
    echo "ERROR: Refusing to act on resource group '${RG_NAME}'. Only 'rg-relay-*' groups are managed by this script."
    exit 1
fi

echo "=============================================================================="
echo "RELAY CLOUD TEARDOWN TOOL"
echo "Target Resource Group: ${RG_NAME}"
echo "=============================================================================="

# 3. Dry-run mode inspection
if [ "${CONFIRM_FLAG}" = "--dry-run" ] || [ "${EXTRA_FLAG}" = "--dry-run" ]; then
    echo "[DRY-RUN MODE ACTIVATED]"
    echo "Would check Azure CLI authentication."
    echo "Would inspect resources in resource group: ${RG_NAME}"
    echo "Would require explicit flag: --confirm-delete-all-resources"
    echo "Would require typing '${RG_NAME}' interactively."
    echo "Would trigger asynchronous deletion of resource group '${RG_NAME}'."
    echo "Key Vault soft-delete retention (90 days) WOULD BE PRESERVED."
    echo "NO CHANGES HAVE BEEN MADE."
    exit 0
fi

if [ "${CONFIRM_FLAG}" != "--confirm-delete-all-resources" ]; then
    echo "SAFETY ABORT: This script permanently deletes ALL cloud resources in ${RG_NAME}."
    echo "To test safely without deleting anything:"
    echo "  $0 ${ENV} --dry-run"
    echo ""
    echo "To execute actual deletion, you must supply the explicit confirmation flag:"
    echo "  $0 ${ENV} --confirm-delete-all-resources"
    exit 1
fi

if ! command -v az &> /dev/null; then
    echo "ERROR: Azure CLI (az) is not installed."
    exit 1
fi

# Verify active subscription
CURRENT_SUB_NAME=$(az account show --query name -o tsv 2>/dev/null || echo "Unknown")
CURRENT_SUB_ID=$(az account show --query id -o tsv 2>/dev/null || echo "Unknown")
echo "Active Subscription: ${CURRENT_SUB_NAME} (${CURRENT_SUB_ID:0:8}...)"

if ! az group exists --name "${RG_NAME}" 2>/dev/null; then
    echo "Resource Group '${RG_NAME}' does not exist. Nothing to teardown."
    exit 0
fi

echo ""
echo "Inventory of resources to be deleted in '${RG_NAME}':"
echo "------------------------------------------------------------------------------"
az resource list --resource-group "${RG_NAME}" --output table
echo "------------------------------------------------------------------------------"
echo "WARNING: Deleting this resource group deletes:"
echo "  1. Azure SQL Database (all data, event logs, deliveries destroyed)."
echo "  2. Azure Container Apps (ca-relay-api, ca-relay-worker) and Environment."
echo "  3. Azure Service Bus namespace and queue."
echo "  4. Azure Storage Accounts (Data Protection blobs and Static Web assets)."
echo "  5. Azure Key Vault (will enter SOFT-DELETE state; keys retained for 90 days)."
echo ""
echo "NOTE ON KEY VAULT & DATA PROTECTION:"
echo "  Key Vault soft-delete is PRESERVED. The cryptographic key 'relay-dp-key' will"
echo "  NOT be purged, allowing data recovery if the vault is restored within 90 days."
echo "=============================================================================="

read -rp "Type the exact resource group name '${RG_NAME}' to proceed with DELETION: " CONFIRM_NAME
if [ "${CONFIRM_NAME}" != "${RG_NAME}" ]; then
    echo "Confirmation failed. Teardown cancelled."
    exit 1
fi

echo "Initiating deletion of Resource Group '${RG_NAME}'..."
az group delete --name "${RG_NAME}" --yes --no-wait
echo "Deletion request accepted by Azure Resource Manager. Deletion is running asynchronously in the background."
echo "Key Vault enters soft-delete state. It is NOT permanently purged."
