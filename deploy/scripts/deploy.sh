#!/usr/bin/env bash
set -euo pipefail

# ==============================================================================
# Relay Azure Cloud Deployment Script (Fedora + Podman / Docker Aware)
# ==============================================================================
# Invariants:
# 1. Separates ACR_NAME (for az acr login) from ACR_SERVER (for image tagging).
# 2. Automatically detects and uses Podman or Docker.
# 3. Supports local-only --dry-run mode without contacting or modifying cloud resources.
# 4. Supports read-only --preflight mode with strict subscription & provider checks.
# 5. Generates unique, traceable per-deployment release tags (<short-sha>-<timestamp>).
# 6. Uses single reliable configuration source: static parameters from .bicepparam
#    with explicit operator overrides for dynamic Entra credentials.
# 7. Implements a 4-Stage What-If sequence: requires explicit approval before creating
#    an empty RG for What-If, and a second explicit approval before live provisioning.
# 8. Single authoritative migration flow: runs bootstrap-users as Entra Admin, updates
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

# Resolve parameter file authoritative source
PARAM_FILE="deploy/parameters/${ENV}.bicepparam"
if [ ! -f "${PARAM_FILE}" ] && [ "${ENV}" = "demo" ] && [ -f "deploy/parameters/dev-demo.bicepparam" ]; then
    PARAM_FILE="deploy/parameters/dev-demo.bicepparam"
fi

# Generate unique, traceable per-deployment release tag
GIT_SHA=$(git rev-parse --short=7 HEAD 2>/dev/null || echo "unknown")
GIT_BRANCH=$(git rev-parse --abbrev-ref HEAD 2>/dev/null || echo "unknown")
GIT_DIRTY=""
# Accurately detect any working-tree dirty state (staged, unstaged, untracked, deleted, renamed)
GIT_STATUS=$(git status --porcelain=v1 --untracked-files=all 2>/dev/null) || GIT_STATUS="FAILED_TO_QUERY_GIT_STATUS"
if [ -n "${GIT_STATUS}" ]; then
    GIT_DIRTY="-dirty"
fi
UTC_TIMESTAMP=$(date -u +%Y%m%dT%H%M%SZ)
RELEASE_TAG="${GIT_SHA}${GIT_DIRTY}-${UTC_TIMESTAMP}"
IMAGE_TAG="${RELEASE_TAG}"

echo "=============================================================================="
echo "RELAY CLOUD DEPLOYMENT ORCHESTRATOR"
echo "Target Environment:     ${ENV}"
echo "Target Region:          ${REGION}"
echo "Resource Group:         ${RG_NAME}"
echo "Deployment Salt:        ${DEPLOYMENT_SALT:-none}"
echo "Parameter File:         ${PARAM_FILE}"
echo "Release Tag:            ${IMAGE_TAG}"
echo "Source Git SHA:         ${GIT_SHA}"
echo "Source Branch:          ${GIT_BRANCH}"
echo "Target Subscription ID: ${TARGET_SUBSCRIPTION_ID:-not-specified}"
echo "Execution Mode:         $( [ "${DRY_RUN}" = true ] && echo "Dry Run (Local Only)" || ( [ "${PREFLIGHT}" = true ] && echo "Preflight (Read-Only Cloud)" || echo "Live Deployment" ) )"
echo "=============================================================================="

# 1. Detect Container Engine (Podman preferred on Fedora)
if command -v podman &> /dev/null; then
    CONTAINER_CLI="podman"
    export DOCKER_COMMAND="podman"
    PODMAN_VER=$(podman --version 2>/dev/null || echo "detected")
    echo "Container Engine:       Podman (${PODMAN_VER})"
elif command -v docker &> /dev/null; then
    CONTAINER_CLI="docker"
    echo "Container Engine:       Docker ($(docker --version))"
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

    # Verify Parameter file exists
    if [ ! -f "${PARAM_FILE}" ]; then
        echo "ERROR: Required parameter file '${PARAM_FILE}' was not found."
        echo "Dry-run validation failed."
        exit 1
    fi
    echo "  - Parameter File:   ${PARAM_FILE} (Found)"

    # Validate Bicep templates if Azure CLI / Bicep is present
    if command -v az &> /dev/null; then
        echo "  - Azure CLI:        Installed"
        echo "  - Validating Bicep compilation (deploy/main.bicep)..."
        if ! az bicep build --file deploy/main.bicep --stdout > /dev/null 2>&1; then
            echo "ERROR: Bicep compilation failed for deploy/main.bicep."
            exit 1
        fi
        echo "    Bicep main template compiles cleanly."

        echo "  - Validating Bicep parameter file (${PARAM_FILE})..."
        if ! az bicep build-params --file "${PARAM_FILE}" --stdout > /dev/null 2>&1; then
            echo "ERROR: Bicep parameter file validation failed for ${PARAM_FILE}."
            exit 1
        fi
        echo "    Bicep parameter file validates cleanly."
    else
        echo "  - Azure CLI / Bicep: Not installed in local PATH"
        echo "------------------------------------------------------------------------------"
        echo "[DRY-RUN INCOMPLETE] Bicep tooling is not installed; Bicep templates and parameter files could not be validated."
        echo "Install Azure CLI and Bicep locally to perform complete offline validation."
        exit 2
    fi

    echo "------------------------------------------------------------------------------"
    echo "[DRY-RUN PASSED] Local validation completed successfully."
    echo "No cloud resources have been inspected, created, modified, or deleted."
    exit 0
fi

# 3. Mandatory Subscription ID & Azure CLI Guard for Cloud Operations
if [ -z "${TARGET_SUBSCRIPTION_ID}" ]; then
    echo ""
    echo "ERROR: Target subscription ID is mandatory for cloud operations (--preflight and live deployment)."
    echo "To prevent accidental deployments to an unintended subscription, you must specify the exact subscription ID:"
    echo "  ./deploy/scripts/deploy.sh ${ENV} ${REGION} --subscription=<YOUR_SUBSCRIPTION_ID>"
    echo "  or export AZURE_SUBSCRIPTION_ID=<YOUR_SUBSCRIPTION_ID>"
    exit 1
fi

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

# Verify Active Subscription against Mandatory Target
if [ "${SUBSCRIPTION_ID}" != "${TARGET_SUBSCRIPTION_ID}" ]; then
    echo ""
    echo "ERROR: Target subscription ID does not match active Azure CLI subscription."
    echo "  Expected Target:     ${TARGET_SUBSCRIPTION_ID}"
    echo "  Active Subscription: ${SUBSCRIPTION_ID} (${SUBSCRIPTION_NAME})"
    echo ""
    echo "Automatic subscription switching is prohibited for safety."
    echo "To switch your active Azure CLI subscription, run:"
    echo "  az account set --subscription ${TARGET_SUBSCRIPTION_ID}"
    exit 1
fi

if [ "${SUBSCRIPTION_STATE}" != "Enabled" ]; then
    echo "ERROR: Active Azure subscription is not in 'Enabled' state (Current state: ${SUBSCRIPTION_STATE})."
    exit 1
fi

SAFE_SUB_ID="${SUBSCRIPTION_ID:0:8}-****-****-****-${SUBSCRIPTION_ID: -8}"
echo "Active Subscription:    ${SUBSCRIPTION_NAME} (${SAFE_SUB_ID}) [${SUBSCRIPTION_STATE}]"
echo "Signed in as:           ${USER_NAME}"
echo "=============================================================================="

# 4. Preflight Inspection Function (Shared by --preflight and live deployment)
run_preflight() {
    echo ""
    echo "=============================================================================="
    echo "DEPLOYMENT PREFLIGHT READ-ONLY ASSESSMENT"
    echo "Target Subscription: ${SUBSCRIPTION_NAME} (${SAFE_SUB_ID})"
    echo "Target Region:       ${REGION}"
    echo "=============================================================================="
    
    local preflight_passed=true

    echo "1. Resolving Entra Administrator Identity..."
    USER_OBJECT_ID=$(az ad signed-in-user show --query id -o tsv 2>/dev/null || az ad user show --id "${USER_NAME}" --query id -o tsv 2>/dev/null || echo "")
    if [ -n "${USER_OBJECT_ID}" ]; then
        echo "   [OK] Entra Admin OID: ${USER_OBJECT_ID}"
    else
        echo "   [BLOCKER] Could not resolve Entra ID Object ID for '${USER_NAME}'."
        preflight_passed=false
    fi

    echo ""
    echo "2. Verifying Required Resource Providers (10 required by Bicep templates)..."
    REQUIRED_PROVIDERS=(
        "Microsoft.App"
        "Microsoft.Authorization"
        "Microsoft.ContainerRegistry"
        "Microsoft.Insights"
        "Microsoft.KeyVault"
        "Microsoft.ManagedIdentity"
        "Microsoft.OperationalInsights"
        "Microsoft.ServiceBus"
        "Microsoft.Sql"
        "Microsoft.Storage"
    )

    for provider in "${REQUIRED_PROVIDERS[@]}"; do
        REG_STATE=$(az provider show --namespace "${provider}" --query "registrationState" -o tsv 2>/dev/null || echo "Unknown")
        if [ "${REG_STATE}" = "Registered" ]; then
            echo "   [OK] ${provider}: ${REG_STATE}"
        else
            echo "   [BLOCKER] ${provider} is not registered (state: '${REG_STATE}')."
            preflight_passed=false
        fi
    done

    echo ""
    echo "3. Verifying Regional Azure SQL Basic Availability..."
    SQL_EDITIONS=$(az sql db list-editions --location "${REGION}" --edition Basic --available -o table 2>/dev/null || true)
    if echo "${SQL_EDITIONS}" | grep -q "Basic"; then
        echo "   [OK] Azure SQL Basic edition is available in '${REGION}' for this subscription."
    else
        echo "   [BLOCKER] Azure SQL Basic edition availability could not be verified in '${REGION}'."
        preflight_passed=false
    fi

    echo ""
    echo "4. Verifying Regional Service Support in '${REGION}'..."
    # Array of namespace:resourceType pairs deployed by Relay IaC
    REGIONAL_CHECKS=(
        "Microsoft.App:containerApps"
        "Microsoft.App:managedEnvironments"
        "Microsoft.Sql:servers"
        "Microsoft.ServiceBus:namespaces"
        "Microsoft.KeyVault:vaults"
        "Microsoft.Storage:storageAccounts"
        "Microsoft.ContainerRegistry:registries"
        "Microsoft.ManagedIdentity:userAssignedIdentities"
        "Microsoft.OperationalInsights:workspaces"
        "Microsoft.Insights:components"
    )

    for item in "${REGIONAL_CHECKS[@]}"; do
        ns="${item%%:*}"
        rtype="${item##*:}"
        LOC_RAW=$(az provider show --namespace "${ns}" --query "resourceTypes[?resourceType=='${rtype}'].locations" -o json 2>/dev/null || true)
        if [ -z "${LOC_RAW}" ]; then
            LOC_RAW=$(az provider show --namespace "${ns}" --query "resourceTypes[?resourceType=='${rtype}'].locations" -o tsv 2>/dev/null || true)
        fi
        
        # Robust, whitespace- and case-insensitive exact matching via jq
        # Handles both JSON array output from real Azure CLI queries and plain TSV/text from mocks.
        # Canonical Azure region names (e.g. 'centralindia') and display names (e.g. 'Central India')
        # are normalized by stripping all non-alphanumeric characters and comparing against the normalized target region.
        is_supported=$(echo "${LOC_RAW}" | jq -R -s -r --arg region "${REGION}" '
            def norm: ascii_downcase | gsub("[^a-z0-9]"; "");
            ($region | norm) as $t |
            (try fromjson catch null) as $parsed |
            if ($parsed != null) then
                ($parsed | flatten) as $arr |
                if ($t | length) > 0 and any($arr[]; (try norm catch "") == $t) then
                    "true"
                else
                    "false"
                end
            else
                (split("\n") | map(split("\t")) | flatten) as $arr |
                if ($t | length) > 0 and any($arr[]; (try norm catch "") == $t) then
                    "true"
                else
                    "false"
                end
            end
        ' 2>/dev/null || echo "false")

        if [ "${is_supported}" = "true" ]; then
            echo "   [OK] ${ns}/${rtype} supported in '${REGION}'."
        else
            echo "   [BLOCKER] ${ns}/${rtype} not reported in '${REGION}'."
            preflight_passed=false
        fi
    done

    echo ""
    echo "5. Regional Quota & Capacity Assessment..."
    echo "   [INFO] Real-time ACA Consumption vCore capacity and student subscription quota limits"
    echo "          cannot be verified programmatically via read-only CLI metadata queries."
    echo "   [UNVERIFIED via CLI] Capacity & Quota (Verify via Azure Portal > Subscriptions > Usage + quotas)."

    echo "=============================================================================="
    if [ "${preflight_passed}" = true ]; then
        echo "[PREFLIGHT PASSED] All verifiable resource providers and regional SKUs are verified."
        return 0
    else
        echo "[PREFLIGHT FAILED] One or more mandatory prerequisites are missing or not registered."
        echo "Resolve the blockers listed above before initiating deployment."
        return 1
    fi
}

# 5. Read-Only Preflight Mode (--preflight)
if [ "${PREFLIGHT}" = true ]; then
    if run_preflight; then
        echo "Preflight check completed successfully. No cloud resources were created or modified."
        exit 0
    else
        exit 1
    fi
fi

# ------------------------------------------------------------------------------
# LIVE DEPLOYMENT SEQUENCE (4-STAGE WHAT-IF & SEPARATE APPROVAL BOUNDARIES)
# ------------------------------------------------------------------------------

# Stage 0: Mandatory Preflight Execution Before Any Cloud Action
echo "[STAGE 0] Enforcing mandatory preflight checks before provisioning..."
if ! run_preflight; then
    echo "ERROR: Preflight validation failed. Aborting live deployment."
    exit 1
fi

# Verify parameter file exists
if [ ! -f "${PARAM_FILE}" ]; then
    echo "ERROR: Required parameter file '${PARAM_FILE}' was not found. Aborting deployment."
    exit 1
fi

# Stage 1: Entra ID Admin Object ID Resolution
echo ""
echo "=============================================================================="
echo "STAGE 1: ENTRA ID ADMIN RESOLUTION"
echo "=============================================================================="
if [ -z "${USER_OBJECT_ID:-}" ]; then
    USER_OBJECT_ID=$(az ad signed-in-user show --query id -o tsv 2>/dev/null || az ad user show --id "${USER_NAME}" --query id -o tsv 2>/dev/null || echo "")
fi
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
echo "Executing What-If preview against '${RG_NAME}' using parameter file '${PARAM_FILE}'..."

if ! az deployment group what-if \
    --resource-group "${RG_NAME}" \
    --template-file deploy/main.bicep \
    --parameters "${PARAM_FILE}" \
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
    --parameters "${PARAM_FILE}" \
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
echo "  Release Tag:        ${IMAGE_TAG}"
echo "=============================================================================="

# Stage 6: Authenticate Container CLI and Build/Push Images
echo "[STEP 2/6] Authenticating ${CONTAINER_CLI} with ACR '${ACR_NAME}'..."
az acr login --name "${ACR_NAME}"

echo "[STEP 3/6] Building and Pushing Container Images with Release Tag '${IMAGE_TAG}' (${CONTAINER_CLI})..."

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
# Use exact managed identity names configured by Bicep ('id-relay-${ENV}-<role>')
IDENTITY_PREFIX="relay-${ENV}"
API_IDENTITY_NAME="id-${IDENTITY_PREFIX}-api"
WORKER_IDENTITY_NAME="id-${IDENTITY_PREFIX}-worker"
MIGRATION_IDENTITY_NAME="id-${IDENTITY_PREFIX}-migration"

echo "Bootstrapping external Managed Identity users in Azure SQL..."
echo "  - API Identity:       ${API_IDENTITY_NAME}"
echo "  - Worker Identity:    ${WORKER_IDENTITY_NAME}"
echo "  - Migration Identity: ${MIGRATION_IDENTITY_NAME}"

dotnet run --project src/Relay.Admin/Relay.Admin.csproj -- \
    bootstrap-users \
    --api-identity "${API_IDENTITY_NAME}" \
    --worker-identity "${WORKER_IDENTITY_NAME}" \
    --migration-identity "${MIGRATION_IDENTITY_NAME}"

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
echo "[STEP 5/6] Updating Container Apps Revisions (ca-relay-api, ca-relay-worker) to Release Tag '${IMAGE_TAG}'..."
az containerapp update --name ca-relay-api --resource-group "${RG_NAME}" --image "${ACR_SERVER}/relay-api:${IMAGE_TAG}"
az containerapp update --name ca-relay-worker --resource-group "${RG_NAME}" --image "${ACR_SERVER}/relay-worker:${IMAGE_TAG}"

echo "[STEP 6/6] Building and Deploying React Dashboard to Static Storage..."
cd frontend/relay-dashboard
npm ci
npm run build
cd ../..

# Upload frontend static distribution bundle to $web container with bounded retry for RBAC propagation
echo "Uploading frontend distribution to '${WEB_STORAGE_ACCOUNT}/\$web'..."
UPLOAD_MAX_ATTEMPTS=6
UPLOAD_ATTEMPT=1
UPLOAD_SUCCESS=false

while [ ${UPLOAD_ATTEMPT} -le ${UPLOAD_MAX_ATTEMPTS} ]; do
    if az storage blob upload-batch \
        --account-name "${WEB_STORAGE_ACCOUNT}" \
        --auth-mode login \
        --destination '$web' \
        --source frontend/relay-dashboard/dist \
        --overwrite; then
        UPLOAD_SUCCESS=true
        break
    else
        echo "  [WARNING] Frontend upload failed (attempt ${UPLOAD_ATTEMPT}/${UPLOAD_MAX_ATTEMPTS}). Storage RBAC assignment may still be propagating in Azure Entra ID..."
        if [ ${UPLOAD_ATTEMPT} -lt ${UPLOAD_MAX_ATTEMPTS} ]; then
            echo "  Waiting 15 seconds before retry..."
            sleep 15
        fi
        UPLOAD_ATTEMPT=$((UPLOAD_ATTEMPT + 1))
    fi
done

if [ "${UPLOAD_SUCCESS}" != true ]; then
    echo "ERROR: Failed to upload frontend assets to '${WEB_STORAGE_ACCOUNT}/\$web' after ${UPLOAD_MAX_ATTEMPTS} attempts."
    exit 1
fi

echo "=============================================================================="
echo "DEPLOYMENT COMPLETED SUCCESSFULLY!"
echo "API Endpoint:        ${API_URL}"
echo "Frontend Dashboard:  ${FRONTEND_URL}"
echo "Release Tag:         ${IMAGE_TAG}"
echo "Next Step: Generate tenant and API key via relay-admin, then run smoke-test.sh."
echo "=============================================================================="
