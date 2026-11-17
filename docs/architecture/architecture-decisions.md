# Relay — Architecture Decisions Summary

This document summarizes the foundational architecture decisions for Relay. Each decision is recorded as an Architecture Decision Record (ADR) under `docs/adr/` with detailed context, options considered, trade-offs, and consequences.

## Index of Architecture Decision Records

| ADR | Title | Status | Date | Core Decision |
| :--- | :--- | :--- | :--- | :--- |
| [ADR-0001](../adr/0001-project-architecture.md) | Modular Monolith vs Microservices | Accepted | 2026-11-17 | Adopt a Clean Architecture Modular Monolith with separate Worker Service process |
| [ADR-0002](../adr/0002-database-choice.md) | SQL Server / Azure SQL Database | Accepted | 2026-11-17 | Use SQL Server for relational guarantees, ACID transactions, and atomic unique constraints |
| [ADR-0003](../adr/0003-api-key-authentication.md) | Machine-to-Machine API Key Authentication | Accepted | 2026-11-17 | Implement custom ASP.NET Core authentication scheme using SHA-256 hash and public prefix lookup |
| [ADR-0004](../adr/0004-transactional-outbox.md) | Transactional Outbox Pattern for Ingestion | Accepted | 2026-11-17 | Atomically commit Event and OutboxMessage inside a single database transaction to solve the dual-write problem |
| [ADR-0005](../adr/0005-react-frontend.md) | React and TypeScript Developer Console | Accepted | 2026-11-17 | Build the developer console using React 19, TypeScript, and Vite with strictly in-memory credential handling |

---

## Architectural Principles

1. **Failure-Oriented Design**:
   Distributed systems fail partially and asynchronously. Relay treats failure as a first-class concern: network partitions, duplicate delivery attempts, and concurrent ingestion requests are anticipated and handled gracefully.

2. **Durable Ingestion**:
   Once Relay responds with `HTTP 202 Accepted`, the event is durably recorded in SQL Server along with an outbox record. Downstream failures do not lose accepted events.

3. **Strict Isolation**:
   Multi-tenancy is enforced at every boundary. Tenant identities are derived solely from validated cryptographic credentials, never from user-supplied payloads or URLs.

4. **KISS & Extensibility**:
   We deliberately avoid premature microservices, service meshes, distributed caches, or complex distributed lock managers. Dependencies must be defensible and directly tied to current requirements while remaining extensible for future phases.
