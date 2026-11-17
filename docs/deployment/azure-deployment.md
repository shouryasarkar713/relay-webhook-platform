# Relay — Azure Deployment Guide (Fedora + Podman / Docker)

## 1. Overview

This guide walks through deploying the complete Relay platform to Microsoft Azure using Bicep, Azure Container Apps, Azure SQL Database, Azure Service Bus, and Azure Key Vault in **Central India (Pune)**.

---

## 2. Step-by-Step Deployment Process

### Step 1: Create Resource Group
Create the resource group in Central India (Pune):
```bash
export LOCATION="centralindia"
export RESOURCE_GROUP="rg-relay-demo"
export ENV_NAME="demo"

az group create --name "${RESOURCE_GROUP}" --location "${LOCATION}" --tags Project=Relay Environment="${ENV_NAME}"
```

---

### Step 2: Configure Bicep Parameters & Entra Admin
Retrieve your Entra ID Object ID and User Principal Name (email) to set yourself as the Azure SQL Entra Administrator:
```bash
export CURRENT_USER_OID=$(az ad signed-in-user show --query id -o tsv)
export CURRENT_USER_UPN=$(az ad signed-in-user show --query userPrincipalName -o tsv)

echo "Entra Admin: ${CURRENT_USER_UPN} (${CURRENT_USER_OID})"
```

Edit `deploy/parameters/dev-demo.bicepparam`:
- Set `entraAdminName = '${CURRENT_USER_UPN}'`
- Set `entraAdminObjectId = '${CURRENT_USER_OID}'`
- Set `location = 'centralindia'`
- (Optional): Set `deploymentSalt = 'v2'` if redeploying into the same resource group to generate fresh globally unique resource names and avoid soft-deleted Key Vault naming collisions.

---

### Step 3: Run Bicep What-If Pre-Flight Inspection (4-Stage Sequence)
In accordance with Relay's deployment guards:
1. **Preflight**: Verify regional provider registrations and SKU availability:
   ```bash
   ./deploy/scripts/deploy.sh demo centralindia --preflight
   ```
2. **Resource Group Preparation**: If `rg-relay-demo` does not exist, an empty resource group must be created to evaluate resource-group-scoped Bicep What-If. Creating an empty resource group provisions zero billable services.
3. **What-If Evaluation**: Preview the exact resources that will be provisioned:
   ```bash
   az deployment group what-if \
     --resource-group "${RESOURCE_GROUP}" \
     --template-file deploy/main.bicep \
     --parameters deploy/parameters/dev-demo.bicepparam
   ```
4. **Authorization**: Review the generated What-If changes. Live provisioning commences only after explicit operator authorization.

---

### Step 4: Provision Azure Infrastructure via Bicep
Execute the infrastructure deployment:
```bash
export DEPLOYMENT_NAME="relay-deploy-$(date +%s)"

az deployment group create \
  --resource-group "${RESOURCE_GROUP}" \
  --template-file deploy/main.bicep \
  --parameters deploy/parameters/dev-demo.bicepparam \
  --name "${DEPLOYMENT_NAME}"
```

Retrieve the deployment outputs:
```bash
export ACR_NAME=$(az deployment group show --resource-group "${RESOURCE_GROUP}" --name "${DEPLOYMENT_NAME}" --query properties.outputs.acrName.value -o tsv)
export ACR_SERVER=$(az deployment group show --resource-group "${RESOURCE_GROUP}" --name "${DEPLOYMENT_NAME}" --query properties.outputs.acrLoginServer.value -o tsv)
export SQL_SERVER=$(az deployment group show --resource-group "${RESOURCE_GROUP}" --name "${DEPLOYMENT_NAME}" --query properties.outputs.sqlServerFqdn.value -o tsv)
export SQL_DB=$(az deployment group show --resource-group "${RESOURCE_GROUP}" --name "${DEPLOYMENT_NAME}" --query properties.outputs.sqlDatabaseName.value -o tsv)
export API_URL=$(az deployment group show --resource-group "${RESOURCE_GROUP}" --name "${DEPLOYMENT_NAME}" --query properties.outputs.apiUrl.value -o tsv)
export WEB_STORAGE_ACCOUNT=$(az deployment group show --resource-group "${RESOURCE_GROUP}" --name "${DEPLOYMENT_NAME}" --query properties.outputs.frontendStorageAccountName.value -o tsv)
export FRONTEND_URL=$(az deployment group show --resource-group "${RESOURCE_GROUP}" --name "${DEPLOYMENT_NAME}" --query properties.outputs.frontendWebEndpoint.value -o tsv)
export MIGRATION_JOB_NAME=$(az deployment group show --resource-group "${RESOURCE_GROUP}" --name "${DEPLOYMENT_NAME}" --query properties.outputs.migrationJobName.value -o tsv)

echo "ACR Name:      ${ACR_NAME}"
echo "ACR Server:    ${ACR_SERVER}"
echo "SQL Server:    ${SQL_SERVER}"
echo "API URL:       ${API_URL}"
echo "Frontend URL:  ${FRONTEND_URL}"
```

---

### Step 5: Build and Push Container Images (Podman on Fedora)
Authenticate your container CLI with ACR:
```bash
# On Fedora with Podman:
export DOCKER_COMMAND=podman
az acr login --name "${ACR_NAME}"

# Build and push API, Worker, and Admin images:
IMAGE_TAG="v1.0.0"

podman build -f src/Relay.Api/Dockerfile -t "${ACR_SERVER}/relay-api:${IMAGE_TAG}" .
podman build -f src/Relay.Worker/Dockerfile -t "${ACR_SERVER}/relay-worker:${IMAGE_TAG}" .
podman build -f src/Relay.Admin/Dockerfile -t "${ACR_SERVER}/relay-admin:${IMAGE_TAG}" .

podman push "${ACR_SERVER}/relay-api:${IMAGE_TAG}"
podman push "${ACR_SERVER}/relay-worker:${IMAGE_TAG}"
podman push "${ACR_SERVER}/relay-admin:${IMAGE_TAG}"
```
*(If using Docker Engine instead of Podman, replace `podman` with `docker`).*

---

### Step 6: Bootstrap Database Users & Execute Schema Migration via Container Apps Job
Connect as the Entra SQL Administrator to bootstrap runtime Managed Identity users, then execute the schema migration using the dedicated Azure Container Apps migration job:
```bash
export ConnectionStrings__RelayDatabase="Server=tcp:${SQL_SERVER},1433;Database=${SQL_DB};Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;"

# 1. Provision external database users for id-relay-api, id-relay-worker, and id-relay-migration (as Entra SQL Admin):
dotnet run --project src/Relay.Admin/Relay.Admin.csproj -- bootstrap-users

# 2. Update Container Apps Migration Job to the newly pushed ACR image:
az containerapp job update \
  --name "${MIGRATION_JOB_NAME}" \
  --resource-group "${RESOURCE_GROUP}" \
  --image "${ACR_SERVER}/relay-admin:${IMAGE_TAG}"

# 3. Trigger one-shot schema migration inside ACA under id-relay-migration (db_owner):
az containerapp job start \
  --name "${MIGRATION_JOB_NAME}" \
  --resource-group "${RESOURCE_GROUP}"

# 4. Verify migration job execution completed successfully:
az containerapp job execution list \
  --name "${MIGRATION_JOB_NAME}" \
  --resource-group "${RESOURCE_GROUP}" \
  --output table
```
> **Security Invariant:** The runtime application identities (`id-relay-api` and `id-relay-worker`) are granted only `db_datareader` and `db_datawriter`. DDL privileges (`db_owner`) are granted strictly to `id-relay-migration`, which runs only inside the ephemeral `job-relay-migration` container. Local operators never perform DDL directly in production.

---

### Step 7: Onboard Initial Tenant and Generate API Key
```bash
# Register demo tenant:
dotnet run --project src/Relay.Admin/Relay.Admin.csproj -- tenant create --name "Acme Corp"

# Generate cryptographically secure API key:
dotnet run --project src/Relay.Admin/Relay.Admin.csproj -- apikey create \
  --tenant-id "<TENANT_ID>" \
  --name "Demo Key"
```
*Note down the generated plain-text API key (`rl_live_...`)!*

---

### Step 8: Update Container Apps Revisions
Roll out the pushed container images to the API and Worker:
```bash
az containerapp update \
  --name ca-relay-api \
  --resource-group "${RESOURCE_GROUP}" \
  --image "${ACR_SERVER}/relay-api:${IMAGE_TAG}"

az containerapp update \
  --name ca-relay-worker \
  --resource-group "${RESOURCE_GROUP}" \
  --image "${ACR_SERVER}/relay-worker:${IMAGE_TAG}"
```

---

### Step 9: Build and Deploy Frontend Dashboard
Build the React 19 static bundle and synchronize it to Azure Storage `$web`:
```bash
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
```

---

### Step 10: Run Automated Smoke Acceptance Tests
Validate end-to-end functionality using the hardened acceptance suite. Export the generated plain-text API key:

```bash
# Export the real API key generated in Step 7 (never hardcoded in scripts):
export RELAY_SMOKE_API_KEY="<YOUR_GENERATED_API_KEY>"

# Execute acceptance tests against the live API:
./deploy/scripts/smoke-test.sh "${API_URL}" https://httpbin.org/post
```

The script evaluates:
1. `API Liveness & Readiness Probes` (`GET /health/live`, `GET /health/ready`)
2. `Authenticated Event Ingestion` (`POST /v1/events` with `X-Api-Key`, `Idempotency-Key`)
3. `Duplicate Request Idempotency Enforcement` (asserts identical `eventId` on duplicate ingestion)
4. `Webhook Endpoint Registration` (`POST /v1/endpoints` with subscribed events)
5. `End-to-End Outbox Fan-out & Delivery Record` (submits event, polls `/v1/deliveries` for delivery record within 30s)
6. `Single Event Query` (`GET /v1/events/{id}`)
7. `Manual Dead-Letter Replay` (explicitly optional: tests replay when dead-lettered records exist; reports `SKIPPED` without error if no dead-lettered deliveries exist).

Each test outputs `PASS`, `FAIL`, or `SKIPPED`. The suite exits with code 0 only when all mandatory tests pass.
