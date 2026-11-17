# Azure Deployment Prerequisites

## 1. Overview

Before deploying Relay to Microsoft Azure, ensure you have an active Azure subscription, necessary permissions, and the required CLI tools installed on your local workstation or CI/CD runner.

---

## 2. Azure Subscription & Permissions

1. **Active Subscription**:
   - An active Microsoft Azure Subscription (Pay-As-You-Go, Visual Studio Enterprise/Professional, or Azure Free Account).
2. **Role Requirements**:
   - The deploying user or CI/CD Service Principal must possess the **Owner** or **User Access Administrator + Contributor** role on the target resource group (or subscription).
   - This role is required because Bicep provisions Role-Based Access Control (RBAC) role assignments (e.g., `AcrPull`, `Key Vault Crypto User`, `Storage Blob Data Contributor`, and `Azure Service Bus Data Sender/Receiver`).

---

## 3. Required CLI Tools & Software

Ensure the following tools are installed:
- **Azure CLI (`az`)**: Version 2.60.0 or higher.
  ```bash
  az version
  ```
- **Bicep CLI**: Version 0.28.0 or higher (installed automatically with Azure CLI).
  ```bash
  az bicep version
  ```
- **.NET 10 SDK**: Version 10.0.100 or higher.
  ```bash
  dotnet --version
  ```
- **Docker Engine**: Docker 24+ or Podman (for building and pushing container images).
- **Node.js & npm**: Node 20 LTS or higher (for building the React dashboard).

---

## 4. Required Azure Resource Providers

Ensure the required Azure resource providers are registered in your subscription:

```bash
az provider register --namespace Microsoft.ContainerApp
az provider register --namespace Microsoft.OperationalInsights
az provider register --namespace Microsoft.KeyVault
az provider register --namespace Microsoft.Storage
az provider register --namespace Microsoft.Sql
az provider register --namespace Microsoft.ServiceBus
az provider register --namespace Microsoft.ManagedIdentity
```

Verify registration status:
```bash
az provider list --query "[?namespace=='Microsoft.ContainerApp' || namespace=='Microsoft.Sql' || namespace=='Microsoft.ServiceBus'].{Namespace:namespace, Status:registrationState}" -o table
```

---

## 5. Azure CLI Login

Authenticate to Azure:
```bash
az login
```

Set the active subscription:
```bash
az account set --subscription "<Your-Subscription-ID-or-Name>"
```
