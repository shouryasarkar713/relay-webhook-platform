#!/usr/bin/env bash
set -euo pipefail

# ==============================================================================
# Relay Azure Cloud Deployment Script (Fedora + Podman / Docker Aware)
# ==============================================================================
# Invariants:
# 1. Separates ACR_NAME (for az acr login) from ACR_SERVER (for image tagging).
# 2. Automatically detects and uses Podman or Docker.
# 3. Supports local-only --dry-run mode without contacting or modifying cloud resources.
# 4. Supports read-only --preflight mode for subscription, provider, and SKU checks.
# 5. Implements a 4-Stage What-If sequence: requires explicit approval before creating
#    an empty RG for What-If, and a second explicit approval before live provisioning.
# 6. Single authoritative migration flow: runs bootstrap-users as Entra Admin, updates
#    Container Apps Job image, triggers job, and verifies Succeeded execution.
# ==============================================================================

ENV="demo"
REGION="centralindia"
DEPLOYMENT_SALT=""
TARGET_SUBSCRIPTION_ID="${AZURE_SUBSCRIPTION_ID:-}"
DRY_RUN=false
PREFLIGHT=false

POSITIONAL_ARGS=()
for arg in "$@"; do
    case "$arg" in
        --dry-run)
            DRY_RUN=true
            ;;
        --preflight)
            PREFLIGHT=true
            ;;
        --salt=*)
            DEPLOYMENT_SALT="${arg#*=}"
            ;;
        --subscription=*|--subscription-id=*)
            TARGET_SUBSCRIPTION_ID="${arg#*=}"
            ;;
        *)
            POSITIONAL_ARGS+=("$arg")
            ;;
    esac
done

if [ ${#POSITIONAL_ARGS[@]} -ge 1 ]; then
    ENV="${POSITIONAL_ARGS[0]}"
fi
if [ ${#POSITIONAL_ARGS[@]} -ge 2 ]; then
    REGION="${POSITIONAL_ARGS[1]}"
fi
if [ ${#POSITIONAL_ARGS[@]} -ge 3 ] && [ -z "${DEPLOYMENT_SALT}" ]; then
    DEPLOYMENT_SALT="${POSITIONAL_ARGS[2]}"
fi
RG_NAME="rg-relay-${ENV}"

echo "=============================================================================="
echo "RELAY CLOUD DEPLOYMENT ORCHESTRATOR"
echo "Target Environment:     ${ENV}"
echo "Target Region:          ${REGION}"
echo "Resource Group:         ${RG_NAME}"
echo "Deployment Salt:        ${DEPLOYMENT_SALT:-none}"
echo "Target Subscription ID: ${TARGET_SUBSCRIPTION_ID:-auto-detect}"
echo "Execution Mode:         $( [ "${DRY_RUN}" = true ] && echo "Dry Run (Local Only)" || ( [ "${PREFLIGHT}" = true ] && echo "Preflight (Read-Only Cloud)" || echo "Live Deployment" ) )"
echo "=============================================================================="

# 1. Detect Container Engine (Podman preferred on Fedora)
if command -v podman &> /dev/null; then
    CONTAINER_CLI="podman"
    export DOCKER_COMMAND="podman"
    PODMAN_VER=$(podman --version 2>/dev/null || echo "detected")
    echo "Container Engine:   Podman (${PODMAN_VER})"
elif command -v docker &> /dev/null; then
    CONTAINER_CLI="docker"
    echo "Container Engine:   Docker ($(docker --version))"
else
    echo "ERROR: Neither Podman nor Docker is installed. Please install Podman (recommended on Fedora) or Docker."
    exit 1
fi

# 2. Local-Only Dry-Run Mode
if [ "${DRY_RUN}" = true ]; then
    echo ""
    echo "[DRY-RUN MODE] Validating local tools, templates, and configurations..."
    echo "  - Container Engine: ${CONTAINER_CLI} (Available)"
    
    # Check .NET SDK
    if command -v dotnet &> /dev/null; then
        echo "  - .NET SDK:         $(dotnet --version) (Available)"
    else
        echo "  - .NET SDK:         Not in PATH"
    fi

    # Validate Bicep templates if Azure CLI is present
    if command -v az &> /dev/null; then
        echo "  - Azure CLI:        Installed"
        echo "  - Validating Bicep compilation (deploy/main.bicep)..."
        if az bicep build --file deploy/main.bicep --stdout > /dev/null 2>&1; then
            echo "    Bicep template compiles cleanly."
        else
            echo "    WARNING: Bicep build check returned an issue."
        fi
    else
        echo "  - Azure CLI:        Not installed (Bicep compilation check skipped)"
    fi

    # Verify Parameter files
    PARAM_FILE="deploy/parameters/${ENV}.bicepparam"
    if [ ! -f "${PARAM_FILE}" ]; then
        PARAM_FILE="deploy/parameters/dev-demo.bicepparam"
    fi
    if [ -f "${PARAM_FILE}" ]; then
        echo "  - Parameter File:   ${PARAM_FILE} (Found)"
    else
        echo "  - Parameter File:   WARNING: ${PARAM_FILE} not found"
    fi

    echo "------------------------------------------------------------------------------"
    echo "[DRY-RUN COMPLETE] Local validation completed successfully."
    echo "No cloud resources have been inspected, created, modified, or deleted."
    exit 0
fi

# 3. Check Azure CLI Installation & Authentication
if ! command -v az &> /dev/null; then
    echo "ERROR: Azure CLI (az) is not installed or not in PATH."
    echo "Install Azure CLI: https://learn.microsoft.com/en-us/cli/azure/install-azure-cli"
    exit 1
fi

ACCOUNT_INFO=$(az account show --output json 2>/dev/null || true)
if [ -z "${ACCOUNT_INFO}" ]; then
    echo "ERROR: Azure CLI is not authenticated. Please run 'az login' first."
    exit 1
fi

SUBSCRIPTION_NAME=$(echo "${ACCOUNT_INFO}" | jq -r .name)
SUBSCRIPTION_ID=$(echo "${ACCOUNT_INFO}" | jq -r .id)
SUBSCRIPTION_STATE=$(echo "${ACCOUNT_INFO}" | jq -r .state)
USER_NAME=$(echo "${ACCOUNT_INFO}" | jq -r .user.name)

# 4. Subscription Identity Guard
if [ -n "${TARGET_SUBSCRIPTION_ID}" ] && [ "${SUBSCRIPTION_ID}" != "${TARGET_SUBSCRIPTION_ID}" ]; then
    echo "ERROR: Target subscription ID does not match active Azure CLI subscription."
    echo "  Expected: ${TARGET_SUBSCRIPTION_ID}"
    echo "  Active:   ${SUBSCRIPTION_ID} (${SUBSCRIPTION_NAME})"
    echo "To switch subscription, run:"
    echo "  az account set --subscription ${TARGET_SUBSCRIPTION_ID}"
    exit 1
fi

if [ "${SUBSCRIPTION_STATE}" != "Enabled" ]; then
    echo "ERROR: Active Azure subscription is not in 'Enabled' state (Current state: ${SUBSCRIPTION_STATE})."
    exit 1
fi

echo "Active Subscription: ${SUBSCRIPTION_NAME} (${SUBSCRIPTION_ID})"
echo "Signed in as:        ${USER_NAME}"
echo "=============================================================================="

# 5. Read-Only Preflight Mode (--preflight)
if [ "${PREFLIGHT}" = true ]; then
    echo "[PREFLIGHT MODE ACTIVATED] Performing read-only inspection against Azure..."
    PREFLIGHT_STATUS="PASSED"

    echo ""
    echo "1. Resolving Entra Administrator Identity..."
    USER_OBJECT_ID=$(az ad signed-in-user show --query id -o tsv 2>/dev/null || az ad user show --id "${USER_NAME}" --query id -o tsv 2>/dev/null || echo "")
    if [ -n "${USER_OBJECT_ID}" ]; then
        echo "   [OK] Entra Admin OID: ${USER_OBJECT_ID}"
    else
        echo "   [BLOCKER] Could not resolve Entra ID Object ID for '${USER_NAME}'."
        PREFLIGHT_STATUS="FAILED"
    fi

    echo ""
    echo "2. Verifying Required Resource Providers (8 required)..."
    PROVIDERS=(
        "Microsoft.App"
        "Microsoft.Sql"
        "Microsoft.ServiceBus"
        "Microsoft.KeyVault"
        "Microsoft.Storage"
        "Microsoft.ContainerRegistry"
        "Microsoft.OperationalInsights"
        "Microsoft.Insights"
    )
    for p in "${PROVIDERS[@]}"; do
        STATE=$(az provider show --namespace "$p" --query registrationState -o tsv 2>/dev/null || echo "NotRegistered")
        if [ "${STATE}" = "Registered" ]; then
            echo "   [OK]      ${p}: ${STATE}"
        else
            echo "   [BLOCKER] ${p}: ${STATE} (Must be registered prior to deployment)"
            PREFLIGHT_STATUS="FAILED"
        fi
    done

    echo ""
    echo "3. Verifying Azure SQL Basic SKU Availability in '${REGION}'..."
    SQL_EDITIONS=$(az sql db list-editions --location "${REGION}" --edition Basic --available -o table 2>/dev/null || true)
    if echo "${SQL_EDITIONS}" | grep -q "Basic"; then
        echo "   [OK] Azure SQL Basic edition is available in '${REGION}' for this subscription."
    else
        echo "   [BLOCKER] Azure SQL Basic edition availability could not be verified in '${REGION}'."
        PREFLIGHT_STATUS="FAILED"
    fi

    echo ""
    echo "4. Verifying Regional Service Support in '${REGION}'..."
    ACA_REGIONS=$(az provider show --namespace Microsoft.App --query "resourceTypes[?resourceType=='containerApps'].locations" -o tsv 2>/dev/null || true)
    if echo "${ACA_REGIONS}" | tr '[:upper:]' '[:lower:]' | grep -q "${REGION}"; then
        echo "   [OK] Microsoft.App/containerApps supported in '${REGION}'."
    else
        echo "   [BLOCKER] Microsoft.App/containerApps not reported in '${REGION}'."
        PREFLIGHT_STATUS="FAILED"
    fi

    SB_REGIONS=$(az provider show --namespace Microsoft.ServiceBus --query "resourceTypes[?resourceType=='namespaces'].locations" -o tsv 2>/dev/null || true)
    if echo "${SB_REGIONS}" | tr '[:upper:]' '[:lower:]' | grep -q "${REGION}"; then
        echo "   [OK] Microsoft.ServiceBus/namespaces supported in '${REGION}'."
    else
        echo "   [BLOCKER] Microsoft.ServiceBus/namespaces not reported in '${REGION}'."
        PREFLIGHT_STATUS="FAILED"
    fi

    echo "=============================================================================="
    if [ "${PREFLIGHT_STATUS}" = "PASSED" ]; then
        echo "[PREFLIGHT PASSED] All required resource providers and regional SKUs are verified."
        echo "No cloud resources were created or modified."
        exit 0
    else
        echo "[PREFLIGHT FAILED] One or more prerequisites are missing or not registered."
        echo "Correct the reported blockers above before initiating deployment."
        exit 1
    fi
fi

# ------------------------------------------------------------------------------
# LIVE DEPLOYMENT SEQUENCE (4-STAGE WHAT-IF & SEPARATE APPROVAL BOUNDARIES)
# ------------------------------------------------------------------------------

# Stage 1: Resolve Entra ID Admin Object ID
USER_OBJECT_ID=$(az ad signed-in-user show --query id -o tsv 2>/dev/null || az ad user show --id "${USER_NAME}" --query id -o tsv 2>/dev/null || echo "")
if [ -z "${USER_OBJECT_ID}" ]; then
    echo "WARNING: Could not automatically resolve Entra ID Object ID for '${USER_NAME}'."
    read -rp "Please enter your Entra ID Object ID for Azure SQL Admin: " USER_OBJECT_ID
fi
echo "Entra Admin OID:    ${USER_OBJECT_ID}"

# Stage 2: Resource Group Preparation with Explicit Preliminary Confirmation
RG_EXISTS=$(az group exists --name "${RG_NAME}" 2>/dev/null || echo "false")

if [ "${RG_EXISTS}" != "true" ]; then
    echo ""
    echo "=============================================================================="
    echo "STAGE 2: RESOURCE GROUP PREPARATION (FIRST DEPLOYMENT)"
    echo "=============================================================================="
    echo "Target Resource Group '${RG_NAME}' does not exist in '${REGION}'."
    echo "Azure Resource Manager requires an existing resource group to evaluate a"
    echo "resource-group-scoped Bicep What-If preview."
    echo ""
    echo "NOTICE: Creating this empty resource group provisions ZERO application services"
    echo "and incurs ZERO cloud charges."
    echo "=============================================================================="
    read -rp "Do you approve creating the empty Resource Group '${RG_NAME}' to execute What-If? (yes/no): " CONFIRM_RG
    if [ "${CONFIRM_RG}" != "yes" ]; then
        echo "Deployment aborted by operator. No resources were created or modified."
        exit 0
    fi

    echo "Creating empty Resource Group '${RG_NAME}' in '${REGION}'..."
    az group create --name "${RG_NAME}" --location "${REGION}" --tags Project=Relay Environment="${ENV}" -o none
    echo "Empty Resource Group '${RG_NAME}' successfully created."
else
    echo "Resource Group '${RG_NAME}' already exists."
fi

# Stage 3: Actual Bicep What-If Preview
echo ""
echo "=============================================================================="
echo "STAGE 3: BICEP WHAT-IF PREVIEW"
echo "=============================================================================="
echo "Executing What-If preview against '${RG_NAME}'..."

if ! az deployment group what-if \
    --resource-group "${RG_NAME}" \
    --template-file deploy/main.bicep \
    --parameters environment="${ENV}" location="${REGION}" entraAdminName="${USER_NAME}" entraAdminObjectId="${USER_OBJECT_ID}" entraAdminType="User" deploymentSalt="${DEPLOYMENT_SALT}"; then
    echo ""
    echo "ERROR: Bicep What-If preview failed or could not be evaluated by Azure."
    echo "Refusing to proceed with live deployment without a validated What-If preview."
    exit 1
fi

# Stage 4: Explicit Live Deployment Approval
echo ""
echo "=============================================================================="
echo "STAGE 4: LIVE DEPLOYMENT AUTHORIZATION"
echo "=============================================================================="
echo "Review the planned resource additions/modifications displayed above."
read -rp "Do you approve the What-If preview plan and authorize live deployment? (yes/no): " CONFIRM_DEPLOY
if [ "${CONFIRM_DEPLOY}" != "yes" ]; then
    echo "Deployment aborted by operator. No application resources were provisioned."
    exit 0
fi

# Stage 5: Execute Bicep Provisioning
echo ""
echo "[STEP 1/6] Provisioning Azure Infrastructure via Bicep..."
DEPLOYMENT_NAME="relay-deploy-$(date +%s)"
DEPLOY_OUTPUT=$(az deployment group create \
    --resource-group "${RG_NAME}" \
    --template-file deploy/main.bicep \
    --parameters environment="${ENV}" location="${REGION}" entraAdminName="${USER_NAME}" entraAdminObjectId="${USER_OBJECT_ID}" entraAdminType="User" deploymentSalt="${DEPLOYMENT_SALT}" \
    --name "${DEPLOYMENT_NAME}" \
    --query properties.outputs -o json)

API_URL=$(echo "${DEPLOY_OUTPUT}" | jq -r .apiUrl.value)
ACR_NAME=$(echo "${DEPLOY_OUTPUT}" | jq -r .acrName.value)
ACR_SERVER=$(echo "${DEPLOY_OUTPUT}" | jq -r .acrLoginServer.value)
FRONTEND_URL=$(echo "${DEPLOY_OUTPUT}" | jq -r .frontendWebEndpoint.value)
WEB_STORAGE_ACCOUNT=$(echo "${DEPLOY_OUTPUT}" | jq -r .frontendStorageAccountName.value)
SQL_SERVER=$(echo "${DEPLOY_OUTPUT}" | jq -r .sqlServerFqdn.value)
SQL_DB=$(echo "${DEPLOY_OUTPUT}" | jq -r .sqlDatabaseName.value)
MIGRATION_JOB_NAME=$(echo "${DEPLOY_OUTPUT}" | jq -r .migrationJobName.value)

echo "Infrastructure Provisioned:"
echo "  ACR Resource Name:  ${ACR_NAME}"
echo "  ACR Login Server:   ${ACR_SERVER}"
echo "  SQL Server FQDN:    ${SQL_SERVER}"
echo "  SQL Database:       ${SQL_DB}"
echo "  API Endpoint:       ${API_URL}"
echo "  Frontend Web:       ${FRONTEND_URL}"
echo "  Migration Job:      ${MIGRATION_JOB_NAME}"
echo "=============================================================================="

# Stage 6: Authenticate Container CLI and Build/Push Images
echo "[STEP 2/6] Authenticating ${CONTAINER_CLI} with ACR '${ACR_NAME}'..."
az acr login --name "${ACR_NAME}"

echo "[STEP 3/6] Building and Pushing Container Images (${CONTAINER_CLI})..."
IMAGE_TAG="v1.0.0"

${CONTAINER_CLI} build -f src/Relay.Api/Dockerfile -t "${ACR_SERVER}/relay-api:${IMAGE_TAG}" .
${CONTAINER_CLI} build -f src/Relay.Worker/Dockerfile -t "${ACR_SERVER}/relay-worker:${IMAGE_TAG}" .
${CONTAINER_CLI} build -f src/Relay.Admin/Dockerfile -t "${ACR_SERVER}/relay-admin:${IMAGE_TAG}" .

${CONTAINER_CLI} push "${ACR_SERVER}/relay-api:${IMAGE_TAG}"
${CONTAINER_CLI} push "${ACR_SERVER}/relay-worker:${IMAGE_TAG}"
${CONTAINER_CLI} push "${ACR_SERVER}/relay-admin:${IMAGE_TAG}"

# Stage 7: Database Security Bootstrapping & Container Apps Migration Job
echo "[STEP 4/6] Executing Azure SQL Database Bootstrap and Container Apps Migration Job..."
export ConnectionStrings__RelayDatabase="Server=tcp:${SQL_SERVER},1433;Database=${SQL_DB};Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;"

# 1. Bootstrap external managed identity users in Azure SQL (as Entra Admin)
echo "Bootstrapping external Managed Identity users in Azure SQL..."
dotnet run --project src/Relay.Admin/Relay.Admin.csproj -- bootstrap-users

# 2. Update Container Apps Migration Job with the newly pushed image
echo "Updating Migration Job image to '${ACR_SERVER}/relay-admin:${IMAGE_TAG}'..."
az containerapp job update \
    --name "${MIGRATION_JOB_NAME}" \
    --resource-group "${RG_NAME}" \
    --image "${ACR_SERVER}/relay-admin:${IMAGE_TAG}"

# 3. Trigger Container Apps Migration Job execution (running under id-relay-migration with db_owner role)
echo "Triggering Migration Job '${MIGRATION_JOB_NAME}'..."
EXECUTION_NAME=$(az containerapp job start \
    --name "${MIGRATION_JOB_NAME}" \
    --resource-group "${RG_NAME}" \
    --query name -o tsv)

echo "Started migration job execution: ${EXECUTION_NAME}"
echo "Waiting for migration job execution to complete..."

JOB_SUCCEEDED=false
CLI_FAILURES=0
for i in $(seq 1 30); do
    STATUS_OUTPUT=$(az containerapp job execution show \
        --name "${MIGRATION_JOB_NAME}" \
        --resource-group "${RG_NAME}" \
        --job-execution-name "${EXECUTION_NAME}" \
        --query properties.status -o tsv 2>/dev/null) || {
        CLI_FAILURES=$((CLI_FAILURES + 1))
        echo "  [WARNING] CLI status query failed (count ${CLI_FAILURES}/5)..."
        if [ ${CLI_FAILURES} -ge 5 ]; then
            echo "ERROR: Repeated CLI failures while querying migration job status."
            exit 1
        fi
        sleep 5
        continue
    }

    CLI_FAILURES=0
    STATUS="${STATUS_OUTPUT:-Unknown}"

    case "${STATUS}" in
        "Succeeded")
            echo "Migration job execution '${EXECUTION_NAME}' succeeded."
            JOB_SUCCEEDED=true
            break
            ;;
        "Failed")
            echo "ERROR: Migration job execution '${EXECUTION_NAME}' failed."
            az containerapp job execution show \
                --name "${MIGRATION_JOB_NAME}" \
                --resource-group "${RG_NAME}" \
                --job-execution-name "${EXECUTION_NAME}" \
                --output json || true
            exit 1
            ;;
        "Running"|"Processing"|"Pending")
            echo "  Status: ${STATUS} (attempt ${i}/30)..."
            ;;
        *)
            echo "  Status: Unknown status '${STATUS}' (attempt ${i}/30)..."
            ;;
    esac
    sleep 5
done

if [ "${JOB_SUCCEEDED}" != true ]; then
    echo "ERROR: Migration job execution timed out or did not report Succeeded status."
    exit 1
fi

# Stage 8: Deploy Container Apps Revisions & Frontend
echo "[STEP 5/6] Updating Container Apps Revisions (ca-relay-api, ca-relay-worker)..."
az containerapp update --name ca-relay-api --resource-group "${RG_NAME}" --image "${ACR_SERVER}/relay-api:${IMAGE_TAG}"
az containerapp update --name ca-relay-worker --resource-group "${RG_NAME}" --image "${ACR_SERVER}/relay-worker:${IMAGE_TAG}"

echo "[STEP 6/6] Building and Deploying React Dashboard to Static Storage..."
cd frontend/relay-dashboard
npm ci
npm run build
cd ../..

az storage blob upload-batch \
    --account-name "${WEB_STORAGE_ACCOUNT}" \
    --auth-mode login \
    --destination '$web' \
    --source frontend/relay-dashboard/dist \
    --overwrite

echo "=============================================================================="
echo "DEPLOYMENT COMPLETED SUCCESSFULLY!"
echo "API Endpoint:        ${API_URL}"
echo "Frontend Dashboard:  ${FRONTEND_URL}"
echo "Next Step: Generate tenant and API key via relay-admin, then run smoke-test.sh."
echo "=============================================================================="
