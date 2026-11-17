# ADR-0009: Durable Event Fan-Out and Consumer Idempotency

- **Status**: Accepted
- **Date**: 2026-11-17

## 1. Context

When an event envelope is published to Azure Service Bus, background workers must consume it, determine which tenant endpoints are subscribed to that event type, and generate individual webhook delivery tasks.

In distributed messaging architectures:
1. Message brokers provide **at-least-once** delivery guarantees under transient network partitions, worker crashes, or lock expirations.
2. If consumer processing restarts after partial execution, the consumer might receive the same message twice.
3. Without strict fan-out idempotency, duplicate broker delivery would generate duplicate delivery rows, causing customers to receive duplicated HTTP webhooks.

## 2. Decision

We implemented a **Database-Backed Idempotent Fan-Out Pipeline** leveraging Azure Service Bus `PeekLock` semantics and SQL Server transactional constraints:

1. **Database Event as Canonical Source of Truth**:
   - The message payload in Azure Service Bus contains an `OutboxEnvelope` (`eventId`, `tenantId`, `eventType`).
   - The consumer queries the primary `Events` table using `EventId`. If the referenced event does not exist, the message is logged and abandoned/dead-lettered.

2. **Durable Idempotency Tracking Entity (`WebhookEventFanout`)**:
   - Before executing fan-out, the consumer checks `WebhookEventFanouts` within a SQL transaction.
   - A unique constraint on `WebhookEventFanout.EventId` ensures that an event is fanned out exactly once.
   - If an insert to `WebhookEventFanout` conflicts with an existing row, the transaction safely detects the duplicate and skips fan-out.

3. **Atomic Transactional Delivery Creation**:
   - For every active endpoint matching `(TenantId == envelope.TenantId AND IsActive == 1 AND Subscriptions.Contains(envelope.EventType))`, a `WebhookDelivery` row is inserted in status `Pending`.
   - A unique database index on `(EventId, EndpointId)` at the SQL Server level physically prevents duplicate delivery rows even under concurrent consumer race conditions.
   - The fan-out transaction commits before the Service Bus message is settled via `receiver.CompleteMessageAsync(message)`.

4. **Two-Stage Decoupled Pipeline**:
   - Fan-out (Stage 1) only creates `WebhookDelivery` rows in SQL Server.
   - Delivery Dispatch (Stage 2) is executed independently by `WebhookDeliveryDispatcher`.
   - This decouples message queue ingestion from external HTTP network latency, ensuring slow customer HTTP endpoints cannot block the Service Bus queue.

## 3. Consequences

### Positive
- Strict idempotency: Duplicate Service Bus messages never create duplicate delivery jobs.
- High resilience: Slow, unresponsive, or offline customer webhook endpoints do not delay the message broker queue.
- Full auditability: `WebhookEventFanout` tracks the exact number of matching endpoints when the event occurred.

### Negative
- Requires a two-stage background pipeline (Service Bus Consumer -> SQL Server Deliveries -> HTTP Dispatcher), increasing operational coordination across worker background services.
