# ADR-0002: Relational Database Selection (SQL Server / Azure SQL)

- **Status**: Accepted
- **Date**: 2026-11-17

## 1. Context

Relay ingests events, tracks multi-tenant ownership, provides API key credentials, and coordinates reliable message dispatch. Crucially, duplicate event prevention relies on atomic enforcement of uniqueness across `(TenantId, IdempotencyKey)`. Furthermore, the Transactional Outbox pattern requires that the business event and the outbox dispatch record commit atomically within the same logical transaction.

## 2. Problem

Which database technology provides the ACID guarantees, relational integrity, enterprise durability, and native Azure ecosystem alignment required for Relay's core operational invariants?

## 3. Decision

We choose **SQL Server** for local development (and containerized integration testing) and **Azure SQL Database** for future production cloud deployments, managed via **Entity Framework Core 10**.

Key technical capabilities utilized:
1. **ACID Transactions**: Full multi-statement atomic transactions ensuring `Event` and `OutboxMessage` succeed or roll back together.
2. **Relational Constraints & Unique Indexes**: Enforcing `UNIQUE (TenantId, IdempotencyKey)` and `UNIQUE (KeyPrefix)` at the database engine level, preventing race conditions that application-level checks cannot prevent.
3. **Optimistic & Row-Level Concurrency**: Predictable row-level locking behavior during high-concurrency inserts.
4. **EF Core Migrations**: Fully auditable, version-controlled C# migration files maintaining production database schemas without manual DDL drift.
5. **Direct Azure Parity**: Local SQL Server containers replicate Azure SQL Database features, syntax, indexing behaviors, and connection resilience.

## 4. Alternatives Considered

1. **Document Database (e.g., Azure Cosmos DB, MongoDB)**:
   - *Pros*: Flexible document payloads.
   - *Cons*: Multi-document transactions across collections are constrained by partition keys and carry high Request Unit (RU) costs. Enforcing cross-document unique constraints across arbitrary partitions is complex. Dual-write atomicity between events and outbox would require artificial partition colocations.
2. **PostgreSQL**:
   - *Pros*: Excellent open-source relational database.
   - *Cons*: While technically capable, SQL Server and Azure SQL Database provide superior first-party alignment with .NET, Azure Managed Identities, Azure Defender for SQL, and enterprise .NET ecosystem benchmarks.

## 5. Consequences & Trade-offs

### Positive
- Bulletproof idempotency enforcement via engine-level unique constraints.
- Atomic Transactional Outbox implementation with zero risk of partial failure.
- Strong schema typing with EF Core 10 and clean migrations.
- Seamless eventual transition to Azure SQL Database with Managed Identity authentication.

### Negative / Trade-offs
- Requires SQL Server container or local instance for running full integration test suites.
- High ingestion throughput will require careful index design (avoiding index fragmentation on GUID primary keys by utilizing sequential GUIDs / index tuning).
- Payload storage in SQL Server has size limits; payloads exceeding 32 KB will eventually be offloaded to Azure Blob Storage via the Claim Check pattern in Phase 6.
