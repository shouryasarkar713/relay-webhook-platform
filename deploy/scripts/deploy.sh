#!/usr/bin/env bash
set -euo pipefail

# ==============================================================================
# Relay Azure Cloud Deployment Script (Fedora + Podman / Docker Aware)
# ==============================================================================
# Invariants:
# 1. Separates ACR_NAME (for az acr login) from ACR_SERVER (for image tagging).
# 2. Automatically detects and uses Podman or Docker.
# 3. Supports --dry-run mode for pre-flight validation without modifying cloud resources.
# 4. Supports optional DEPLOYMENT_SALT for safe redeployment without Key Vault collisions.
# ==============================================================================

ENV="demo"
REGION="centralindia"
DEPLOYMENT_SALT=""
DRY_RUN=false

POSITIONAL_ARGS=()
for arg in "$@"; do
    case "$arg" in
        --dry-run)
            DRY_RUN=true
            ;;
        --salt=*)
            DEPLOYMENT_SALT="${arg#*=}"
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
echo "Target Environment: ${ENV}"
echo "Target Region:      ${REGION}"
echo "Resource Group:     ${RG_NAME}"
echo "Deployment Salt:    ${DEPLOYMENT_SALT:-none}"
echo "Dry Run Mode:       ${DRY_RUN}"
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

# 2. Check Azure CLI
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
USER_NAME=$(echo "${ACCOUNT_INFO}" | jq -r .user.name)

echo "Active Subscription: ${SUBSCRIPTION_NAME} (${SUBSCRIPTION_ID:0:8}...)"
echo "Signed in as:        ${USER_NAME}"
echo "=============================================================================="

if [ "${DRY_RUN}" = true ]; then
    echo "[DRY-RUN COMPLETE] Local tools and Azure CLI session verified successfully."
    echo "No cloud resources have been created or modified."
    exit 0
fi

# 3. Ensure Resource Group
echo "[STEP 1/8] Ensuring Resource Group '${RG_NAME}' in '${REGION}'..."
az group create --name "${RG_NAME}" --location "${REGION}" --tags Project=Relay Environment="${ENV}" -o none

# 4. Resolve Entra ID Admin Object ID
USER_OBJECT_ID=$(az ad signed-in-user show --query id -o tsv 2>/dev/null || az ad user show --id "${USER_NAME}" --query id -o tsv 2>/dev/null || echo "")

if [ -z "${USER_OBJECT_ID}" ]; then
    echo "WARNING: Could not automatically resolve Entra ID Object ID for '${USER_NAME}'."
    read -rp "Please enter your Entra ID Object ID for Azure SQL Admin: " USER_OBJECT_ID
fi

echo "Entra Admin OID:    ${USER_OBJECT_ID}"

# 5. Run What-If Preview
echo "[STEP 2/8] Running Bicep What-If Preview..."
az deployment group what-if \
    --resource-group "${RG_NAME}" \
    --template-file deploy/main.bicep \
    --parameters environment="${ENV}" location="${REGION}" entraAdminName="${USER_NAME}" entraAdminObjectId="${USER_OBJECT_ID}" entraAdminType="User" deploymentSalt="${DEPLOYMENT_SALT}"

read -rp "Do you approve this deployment plan? (yes/no): " CONFIRM
if [ "${CONFIRM}" != "yes" ]; then
    echo "Deployment aborted by operator."
    exit 0
fi

# 6. Execute Bicep Provisioning
echo "[STEP 3/8] Provisioning Azure Infrastructure via Bicep..."
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

# 7. Authenticate Container CLI with ACR
echo "[STEP 4/8] Authenticating ${CONTAINER_CLI} with ACR '${ACR_NAME}'..."
az acr login --name "${ACR_NAME}"

# 8. Build and Push Container Images using detected Container CLI
echo "[STEP 5/8] Building and Pushing Container Images (${CONTAINER_CLI})..."
IMAGE_TAG="v1.0.0"

${CONTAINER_CLI} build -f src/Relay.Api/Dockerfile -t "${ACR_SERVER}/relay-api:${IMAGE_TAG}" .
${CONTAINER_CLI} build -f src/Relay.Worker/Dockerfile -t "${ACR_SERVER}/relay-worker:${IMAGE_TAG}" .
${CONTAINER_CLI} build -f src/Relay.Admin/Dockerfile -t "${ACR_SERVER}/relay-admin:${IMAGE_TAG}" .

${CONTAINER_CLI} push "${ACR_SERVER}/relay-api:${IMAGE_TAG}"
${CONTAINER_CLI} push "${ACR_SERVER}/relay-worker:${IMAGE_TAG}"
${CONTAINER_CLI} push "${ACR_SERVER}/relay-admin:${IMAGE_TAG}"

# 9. Database Bootstrap & Migration
echo "[STEP 6/8] Executing Azure SQL Database Bootstrap and Migrations..."
export ConnectionStrings__RelayDatabase="Server=tcp:${SQL_SERVER},1433;Database=${SQL_DB};Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;"

# Bootstrap external managed identity users in Azure SQL (as Entra Admin)
dotnet run --project src/Relay.Admin/Relay.Admin.csproj -- bootstrap-users

# Run migration
dotnet run --project src/Relay.Admin/Relay.Admin.csproj -- migrate

# 10. Deploy Container Apps Revisions
echo "[STEP 7/8] Updating Container Apps Revisions..."
az containerapp update --name ca-relay-api --resource-group "${RG_NAME}" --image "${ACR_SERVER}/relay-api:${IMAGE_TAG}"
az containerapp update --name ca-relay-worker --resource-group "${RG_NAME}" --image "${ACR_SERVER}/relay-worker:${IMAGE_TAG}"

# 11. Deploy React 19 Frontend
echo "[STEP 8/8] Building and Deploying React Dashboard to Static Storage..."
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
echo "=============================================================================="
