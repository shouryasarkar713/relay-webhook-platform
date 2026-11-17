# ADR-0014: Webhook Delivery Dead-Letter Management and Manual Replay

- **Status**: Accepted
- **Date**: 2026-11-17

## 1. Context

When an endpoint experiences extended downtime or unrecoverable bugs, repeated retries will eventually exhaust the retry budget (e.g. 8 attempts) or exceed the 24-hour maximum delivery window. Continuously retrying exhausted deliveries permanently clogs background dispatch queues.

However, completely discarding exhausted events destroys critical business data (e.g., missed payment notifications or order cancellations). Operations and tenant developers require the ability to:
1. Isolate exhausted deliveries into a terminal dead-letter state.
2. Query and inspect dead-lettered events and their full attempt history.
3. Trigger a controlled manual replay once the endpoint has been repaired, without losing attempt history or violating idempotency.

## 2. Decision

We implemented a dedicated Dead-Letter lifecycle and tenant-authorized Replay API:

1. **Dead-Letter State (`WebhookDeliveryStatus.DeadLettered`)**:
   - A delivery enters `DeadLettered` status when:
     - The retry attempt counter reaches `DeliveryRetryOptions.MaxAttempts`.
     - The delivery age exceeds `DeliveryRetryOptions.MaxRetryWindowHours`.
     - The server requested a `Retry-After` delay exceeding `MaxServerRequestedDelaySeconds`.
   - The delivery's `DeadLetteredAtUtc` timestamp is populated.
   - The worker's claim query strictly ignores `DeadLettered` deliveries, stopping all background automated attempts.

2. **Durable Manual Replay API (`POST /v1/deliveries/{deliveryId}/replay`)**:
   - **Tenant Authorization**: The request must authenticate with a valid tenant API key; tenants can only replay their own deliveries.
   - **Eligibility Check**: Only deliveries in `DeadLettered` or `Failed` status can be replayed. Attempting to replay an active (`Pending`, `Processing`) or already delivered (`Delivered`) record returns `409 Conflict`.
   - **Active Endpoint Verification**: Verifies that the delivery's target endpoint still exists and is currently active (`IsActive == true`). If deactivated, replay is rejected with `409 Conflict`.
   - **Idempotency Protection**: Accepts an optional `Idempotency-Key` header. Duplicate replay requests with the same key return the original `ReplayDeliveryResponse` idempotently.
   - **Audit Trail (`WebhookDeliveryReplayAudit`)**:
     - Inserts a durable audit row recording `TenantId`, `DeliveryId`, `ReplayKey`, `RequestedBy`, `PreviousStatus`, and `ReplayedAtUtc`.
   - **State Transition**:
     - Delivery status is reset to `Pending`.
     - `ReplayCount` is incremented.
     - `DeadLetteredAtUtc` is cleared (`null`).
     - `NextAttemptAtUtc` is set to `replayedAtUtc` for immediate pickup.
     - The stable `DeliveryId` and all previous `WebhookDeliveryAttempts` are **strictly preserved**, providing an unbroken forensic audit log across the delivery's lifetime.

3. **Dashboard Observability**:
   - Deliveries view includes a dedicated "Dead-Lettered" status filter.
   - One-click "Replay" action button with confirmation dialog for dead-lettered and failed records.

## 3. Consequences

### Positive
- Prevents poison-pill messages and offline endpoints from monopolizing background dispatcher capacity.
- Zero data loss for failed deliveries.
- Complete forensic auditability: replay actions are logged and attempt history is never overwritten.
- Idempotency guarantees protect against accidental double-clicks or automated retry loops in tenant administration tools.

### Negative / Trade-offs
- Table growth in `WebhookDeliveryAttempts` and `WebhookDeliveryReplayAudits`, managed through database indexing.
