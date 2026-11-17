# Relay — Cloud Security Model

## 1. Overview

Relay adheres to a Zero-Trust security posture designed to eliminate shared secrets, enforce least-privilege access, prevent lateral movement in the event of container compromise, and mitigate common cloud vulnerabilities (including Server-Side Request Forgery and data leakage).

---

## 2. Zero-Trust Identity & Access Management

### 2.1 Passwordless Managed Identities
Relay completely eliminates long-lived connection strings, database passwords, and Service Bus shared access policy keys from application configurations. All service-to-service communication is authenticated via **User-Assigned Managed Identities** issued by Microsoft Entra ID.

Three distinct identities are provisioned:
1. `id-relay-api`: Assigned to `ca-relay-api`.
2. `id-relay-worker`: Assigned to `ca-relay-worker`.
3. `id-relay-migration`: Assigned to the migration tool / deployment pipeline.

### 2.2 Least-Privilege Role-Based Access Control (RBAC) Matrix

| Azure Resource | Role Definition | `id-relay-api` | `id-relay-worker` | `id-relay-migration` | Rationale |
| :--- | :--- | :---: | :---: | :---: | :--- |
| **Azure Container Registry** | `AcrPull` | Yes | Yes | No | Required for ACA runtime to pull application images. |
| **Azure Key Vault** | `Key Vault Crypto User` | Yes | Yes | No | Allows wrapping/unwrapping Data Protection master keys (`relay-dp-key`). No secret read/write or admin permissions. |
| **Azure Blob Storage** (`strelaydp`) | `Storage Blob Data Contributor` | Yes | Yes | No | Allows reading and writing XML key ring files in the `dataprotection-keys` container. |
| **Azure Service Bus** | `Azure Service Bus Data Sender` | **No** | **Yes** | **No** | API never interacts with Service Bus directly (writes only to SQL Outbox). Worker sends messages during outbox dispatch. |
| **Azure Service Bus** | `Azure Service Bus Data Receiver` | **No** | **Yes** | **No** | Worker consumes messages from `relay-events` queue. |
| **Azure SQL Database** | `db_datareader` & `db_datawriter` | **Yes** | **Yes** | **Yes** | Application runtimes only need DML permissions to query and insert records. |
| **Azure SQL Database** | `db_ddladmin` / `db_owner` | **No** | **No** | **Yes** | Schema migrations require DDL permissions. Runtime apps are strictly forbidden from modifying tables or schema. |

---

## 3. Data Protection and Secret Management

### 3.1 Encryption at Rest
- **Azure SQL Database**: Protected by Transparent Data Encryption (TDE) with service-managed or customer-managed keys (AES-256).
- **Azure Storage**: All blob containers are encrypted at rest using Microsoft-managed encryption keys.
- **Webhook Secrets**: Tenant webhook secrets are encrypted at rest within the `WebhookEndpoints` table using ASP.NET Core Data Protection before being persisted.

### 3.2 ASP.NET Core Data Protection Key Ring
- Rather than relying on local container file systems (which are ephemeral and isolated per replica), the Data Protection key ring is persisted to an Azure Blob Storage container.
- Key ring XML files are encrypted at rest using an asymmetric RSA 2048-bit key stored in Azure Key Vault (`relay-dp-key`).
- Both `ca-relay-api` and `ca-relay-worker` use identical Data Protection application names (`Relay.Platform`), enabling seamless cross-replica and cross-service decryption of secrets and security tokens.

### 3.3 Zero Secrets in Configuration
- Application settings (`appsettings.json`, environment variables) contain only resource identifiers, endpoints, and URIs (e.g., `https://kv-relay-prod.vault.azure.net/`).
- No passwords, client secrets, API tokens, or SAS tokens are stored in environment variables, configuration files, or Git repositories.

---

## 4. Ingress, Network Security & SSRF Defense

### 4.1 Ingress Security
- Public ingress is restricted strictly to `ca-relay-api`.
- The ACA Envoy proxy enforces HTTPS, terminates TLS using TLS 1.2 or higher, and rejects unencrypted HTTP requests.
- `ca-relay-worker` has ingress disabled entirely. It cannot receive inbound connections from either the public internet or adjacent containers.

### 4.2 Application-Level SSRF Protection
Because Relay allows tenants to supply arbitrary destination URLs for webhook deliveries, the worker is exposed to potential Server-Side Request Forgery (SSRF) threats. 

Relay mitigates SSRF at the application layer through `SsrfSafeHttpClientHandler`:
- **Protocol Restriction**: Only `https://` is permitted in cloud environments.
- **DNS Resolution Pre-Check**: Hostnames are resolved to IP addresses before initiating HTTP requests.
- **Strict IP Validation**: Any IP resolving to internal or restricted ranges is immediately rejected:
  - Azure Instance Metadata Service (`169.254.169.254`).
  - Private networks (RFC 1918: `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`).
  - Loopback (`127.0.0.0/8`, `::1`).
  - Link-local (`169.254.0.0/16`, `fe80::/10`).
  - Carrier-grade NAT (`100.64.0.0/10`).
  - Multicast and broadcast spaces.
- **DNS Rebinding Prevention**: Outbound TCP sockets connect directly to the pre-validated IP address, neutralizing Time-of-Check to Time-of-Use (TOCTOU) DNS rebinding attacks.

---

## 5. Container & Runtime Security

1. **Non-Root Execution**:
   - Both API and Worker containers run under the unprivileged built-in user `USER $APP_UID` (UID 1654).
   - If an attacker achieves code execution inside the container, they do not possess root privileges and cannot modify host-level files or install OS packages.
2. **Minimal Base Images**:
   - Built on official Microsoft ASP.NET Core and .NET Runtime Alpine/Debian slim images, minimizing installed packages, shell utilities, and attack surface.
3. **Automated Vulnerability Scanning**:
   - Container images are scanned during CI build pipelines using GitHub Action security workflows and Trivy/Dependabot vulnerability scanners.
