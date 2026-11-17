# ADR-0020: Managed Identity and Passwordless Cloud Access

- **Status**: Accepted
- **Date**: 2026-11-17

## 1. Context

Traditional cloud applications frequently rely on connection strings containing embedded passwords, access keys, or SAS tokens (e.g. SQL Server connection strings with `User Id=...;Password=...` or Service Bus Shared Access Signatures). 

This introduces critical security vulnerabilities:
1. Credentials risk being accidentally committed to source control or logged in diagnostic telemetry.
2. Credentials require periodic secret rotation, which causes application downtime or complex key rotation plumbing.
3. Leaked credentials grant long-lived, uncontrollable access to the underlying cloud resources.

## 2. Decision

We implemented a strictly **passwordless authentication architecture** across the entire Relay platform using **User-Assigned Managed Identities** and Microsoft Entra ID:

1. **Separation of Managed Identities**:
   - `id-relay-api`: Dedicated identity for `Relay.Api`.
   - `id-relay-worker`: Dedicated identity for `Relay.Worker`.
   - `id-relay-migration`: Dedicated identity for administrative database migrations.
   - Separate identities enforce the principle of least privilege: the API identity is not granted permission to publish or receive Service Bus messages, while the Worker identity is not granted public ingress access.

2. **Azure SQL Database Passwordless Authentication**:
   - Connection strings omit usernames and passwords:
     ```text
     Server=tcp:sql-relay-xxxx.database.windows.net,1433;Database=sqldb-relay;Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;
     ```
   - `Microsoft.Data.SqlClient` leverages `Azure.Identity.DefaultAzureCredential` to acquire Entra OAuth tokens for `https://database.windows.net/.default`.
   - Database user principals are provisioned directly for each Managed Identity, granting only `db_datareader` and `db_datawriter` roles without administrative privileges.

3. **Azure Service Bus Passwordless Messaging**:
   - The worker connects using `ServiceBusClient(fullyQualifiedNamespace, new DefaultAzureCredential())`.
   - RBAC is scoped to the specific queue (`relay-events`):
     - `Azure Service Bus Data Sender` (Worker only)
     - `Azure Service Bus Data Receiver` (Worker only)

4. **Container Registry Access**:
   - Container Apps environment pulls container images from Azure Container Registry using the User-Assigned Identity with the `AcrPull` role, eliminating administrative registry passwords.

## 3. Consequences

### Positive
- Zero committed passwords, SAS tokens, or secret strings in configuration files or container environment variables.
- Automatic, seamless credential rotation managed by the Azure platform.
- Auditable Azure Entra access logs for all database and messaging operations.
- Least-privilege separation across API, Worker, and Migration roles.

### Negative / Trade-offs
- Local development without internet or Azure CLI credentials requires the local SQL Server and Service Bus emulator fallback.
