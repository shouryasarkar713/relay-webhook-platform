# ADR-0004: Transactional Outbox Pattern for Durable Ingestion

- **Status**: Accepted
- **Date**: 2026-11-17

## 1. Context

When an event ingestion API accepts a payload, it must both persist the event in durable storage and notify downstream processing workers (such as an Azure Service Bus queue, RabbitMQ exchange, or background webhook dispatcher).

A naive implementation attempts a "dual-write":
```csharp
await _database.SaveAsync(event);
await _messageBroker.PublishAsync(event); // If this fails or API crashes, message is lost!
```
Conversely:
```csharp
await _messageBroker.PublishAsync(event);
await _database.SaveAsync(event); // If this fails, broker has an orphan ghost message!
```
Distributed transactions across heterogeneous systems (2-Phase Commit / XA) are notoriously fragile, unsupported by modern cloud message brokers, and severely impair ingestion availability and latency.

## 2. Problem

How can Relay guarantee that every accepted event is guaranteed to be forwarded to downstream webhook delivery pipelines, even in the event of worker crashes, message broker outages, network timeouts, or process restarts, without distributed transactions?

## 3. Decision

We implement the **Transactional Outbox Pattern**:

```text
[HTTP Request]
       │
       ▼
[Begin EF Core Transaction]
  ├── INSERT INTO Events (Id, TenantId, EventType, PayloadJson, PayloadHash, IdempotencyKey, Status, CreatedAtUtc)
  └── INSERT INTO OutboxMessages (Id, EventId, TenantId, MessageType, PayloadJson, CreatedAtUtc, Status)
[Commit Transaction]
       │
       ▼
[Return HTTP 202 Accepted]
```

1. **Atomic Insertion**:
   The `Event` and its corresponding `OutboxMessage` record are saved within the same local SQL Server database transaction. The transaction commits if and only if both records succeed.
2. **Acceptance Invariant**:
   The API returns `HTTP 202 Accepted` only after the database transaction successfully commits. If the transaction rolls back, no client receives an acceptance acknowledgement.
3. **Decoupled Asynchronous Publishing (Phase 2)**:
   In Phase 2, a background dispatcher (`Relay.Worker`) queries pending outbox messages (`WHERE Status = 'Pending' ORDER BY CreatedAtUtc`), publishes them to Azure Service Bus or executes delivery, and updates the outbox status to `Published`.
4. **At-Least-Once Delivery**:
   If the dispatcher crashes after publishing to the broker but before updating the outbox table, the message will be republished upon restart. Therefore, message consumers must be idempotent.

## 4. Alternatives Considered

1. **Direct Broker Publishing in HTTP Request**:
   - *Pros*: Apparent simplicity.
   - *Cons*: Prone to the dual-write problem. An outage in the message broker immediately causes the entire event ingestion API to fail, degrading ingestion availability from 99.99% to the availability product of both systems.
2. **Listen to Change Data Capture (CDC) / Debezium**:
   - *Pros*: Zero outbox table overhead; directly captures SQL transaction logs.
   - *Cons*: High infrastructure complexity (requires Kafka/Debezium/Azure Event Hubs connectors), difficult local development story, and opaque message formatting.

## 5. Consequences & Trade-offs

### Positive
- Ingestion is decoupled from message broker availability. Relay can accept events even if Azure Service Bus is undergoing maintenance or experiencing a transient outage.
- 100% durability: No event accepted by Relay is ever lost before publishing.
- Straightforward testing: Integration tests verify outbox persistence by querying the database directly.

### Negative / Trade-offs
- Outbox messages consume database storage until archived or pruned. A cleanup/retention job must be implemented in future phases.
- Polling the outbox table can cause database contention at extreme scale if not indexed appropriately (`(Status, CreatedAtUtc)` compound index is required).
