# ADR-0024: Database Migration and Operational Tooling Strategy

- **Status**: Accepted
- **Date**: 2026-11-17

## 1. Context

In a production cloud environment, applying Entity Framework Core schema migrations requires careful operational discipline. In naive setups, applications frequently call `dbContext.Database.Migrate()` or `Database.MigrateAsync()` during application startup inside `Program.cs`.

In a distributed, multi-replica cloud environment like Azure Container Apps, startup migrations introduce severe operational hazards:
1. **Concurrency Races & Deadlocks**: When multiple replicas of `Relay.Api` or `Relay.Worker` start simultaneously during autoscaling or deployment rollouts, they race to acquire database schema locks and inspect `__EFMigrationsHistory`, leading to startup crashes and transient deployment failures.
2. **Excessive Security Privileges**: Running migrations at runtime requires the application's runtime database identity to possess Schema Modification (DDL) privileges (`db_ddladmin` or `db_owner`). Under the principle of least privilege, application runtimes should only have Data Manipulation (DML) privileges (`db_datareader`, `db_datawriter`, and `EXECUTE`).
3. **Unpredictable Startup Latency**: Large migrations blocking web host startup can trigger Container Apps readiness/liveness probe failures, resulting in restart loops (CrashLoopBackOff).
4. **Lack of Operational Control & Rollback Gates**: Schema migrations should execute deterministically before new application code revisions receive traffic, with explicit failure handling and verification.

## 2. Decision

We decoupled schema migrations and tenant management completely from runtime application startup:

1. **Strict Prohibition of Startup Migrations**:
   - `Program.cs` in both `Relay.Api` and `Relay.Worker` is forbidden from calling `dbContext.Database.Migrate()`.
   - Applications assume the schema is already at the desired migration level when container instances boot.

2. **Dedicated CLI & Operational Tool (`Relay.Admin`)**:
   - Implemented a dedicated executable console application: `src/Relay.Admin`.
   - The CLI provides commands for operational maintenance:
     - `relay-admin migrate`: Applies pending EF Core migrations idempotently using `RelayDbContext`.
     - `relay-admin tenant create <name>`: Provisions a new tenant organization.
     - `relay-admin tenant list`: Lists registered tenants and their statuses.
     - `relay-admin apikey create <tenantId> <description>`: Generates a cryptographically secure 256-bit API key for a tenant.
     - `relay-admin apikey list <tenantId>`: Lists active API keys and their SHA-256 prefixes.
     - `relay-admin apikey revoke <keyId>`: Immediately revokes an API key.

3. **Separation of Privileges & Managed Identities**:
   - Dedicated User-Assigned Managed Identity: `id-relay-migration`.
   - Assigned elevated DDL role (`db_owner` / `db_ddladmin`) on Azure SQL Database for migration execution.
   - Runtime identities `id-relay-api` and `id-relay-worker` are restricted strictly to DML roles (`db_datareader`, `db_datawriter`, and execution of stored procedures if applicable), eliminating the risk of accidental schema corruption or unauthorized DDL operations from exploited web containers.

4. **Deployment Pipeline Integration**:
   - In CI/CD (`deploy.yml`), migrations are executed as a discrete gate prior to rolling out new Container App revisions.
   - Migrations can be executed via:
     1. Azure Container Apps Job (`job-relay-migration`) executing `src/Relay.Admin/Dockerfile`.
     2. GitHub Actions runner running `dotnet run --project src/Relay.Admin -- migrate` using Azure Service Principal / Entra ID federated credentials.
     3. Manual operator execution via Azure Cloud Shell or secure administrative bastion.

## 3. Consequences

### Positive
- Zero race conditions during multi-replica autoscaling or rolling deployments.
- Adheres to the principle of least privilege: runtime containers cannot alter schema or drop tables even if compromised.
- Deterministic, observable migration step in CI/CD pipelines with clear logging and rollback boundaries.
- Centralized, auditable administrative tooling for onboarding tenants and rotating API keys without direct SQL console access.

### Negative / Trade-offs
- Requires maintaining an additional console project (`Relay.Admin`) and its corresponding container image.
- Deployment pipelines must orchestrate the migration step before updating application container revisions.
