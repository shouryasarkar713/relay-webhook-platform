# ADR-0018: Manual Replay, Retry-Cycle Semantics, and Idempotency Scope

- **Status**: Accepted
- **Date**: 2026-11-17

## 1. Context

In Phase 4, operators were provided the ability to manually replay dead-lettered or failed webhook deliveries via `POST /v1/deliveries/{id}/replay`.

However, our reliability audit identified three critical edge cases:
1. **The Stale Event Replay Trap**:
   - `WebhookRetryPolicy` enforced a hard 24-hour retry window calculated from `delivery.CreatedAtUtc` (`CreatedAtUtc + MaxRetryWindowHours`).
   - If an event failed 3 days ago and was replayed today by an operator, the very first transient error (e.g. 503) caused `EvaluateRetry` to check `now > CreatedAtUtc + 24h`.
   - The delivery immediately dead-lettered on attempt 1 without retrying! Replaying stale events was effectively broken for transient failures.
2. **Backoff Explosion on Replay**:
   - If `AttemptCount` was 5 before dead-lettering, replaying caused the first attempt of the new cycle to be evaluated as `AttemptCount = 6`.
   - The retry policy either exceeded `MaxAttempts` immediately or calculated the maximum backoff delay rather than starting from the initial backoff.
3. **Replay Idempotency Key Cross-Delivery Collisions**:
   - The database unique constraint was previously defined as `(TenantId, ReplayKey)`.
   - If an automated batch replay script used the same correlation key `batch-01` across multiple dead-lettered deliveries, only the first delivery replayed; subsequent deliveries returned the first delivery's audit record without replaying!

## 2. Decision

We refined replay semantics, separating lifetime attempt auditing from retry cycle budgeting:

1. **Dual Attempt Tracking**:
   - `AttemptCount`: Monotonically increasing counter across the entire lifetime of the delivery. Never resets. Used by `WebhookDeliveryAttempt.AttemptNumber` for audit trails and idempotency tracking.
   - `RetryCycleAttemptCount`: Counter scoped strictly to the active retry cycle. Starts at 0 upon delivery creation, increments with each attempt in the cycle, and **resets to 0 upon manual replay**.
   - `RetryCycleStartedAtUtc`: Timestamp recording when the current retry cycle began (either initial `CreatedAtUtc` or `replayedAtUtc`).

2. **Fresh 24-Hour Retry Window on Replay**:
   - `WebhookDelivery.Replay(replayedAtUtc)` resets:
     ```csharp
     Status = WebhookDeliveryStatus.Pending;
     RetryCycleAttemptCount = 0;
     RetryCycleStartedAtUtc = replayedAtUtc;
     NextAttemptAtUtc = replayedAtUtc;
     DeadLetteredAtUtc = null;
     ReplayCount++;
     ```
   - In `WebhookDeliveryDispatcher`:
     ```csharp
     var nextRetryAttempt = delivery.RetryCycleAttemptCount + 1;
     var retryStart = delivery.RetryCycleStartedAtUtc;
     var retryDecision = _retryPolicy.EvaluateRetry(nextRetryAttempt, retryStart, parsedRetryAfter);
     ```
   - Replayed deliveries receive a full fresh 24-hour window and exponential backoff starting from attempt 1.

3. **Per-Delivery Idempotency Key Scoping**:
   - Updated the EF Core configuration and SQL Server unique filtered index on `WebhookDeliveryReplayAudits` to:
     ```csharp
     builder.HasIndex(r => new { r.TenantId, r.DeliveryId, r.ReplayKey })
            .IsUnique()
            .HasFilter("[ReplayKey] IS NOT NULL");
     ```
   - Replay idempotency is strictly scoped to the tuple `(TenantId, DeliveryId, ReplayKey)`. The same batch correlation key can be used across multiple distinct deliveries safely.
   - Handled concurrent replay races gracefully by catching `DbUpdateException` and returning the existing audit row without crashing the API.

## 3. Consequences

### Positive
- Replayed deliveries consistently receive a full retry budget and fresh 24-hour window regardless of event age.
- Lifetime audit history remains completely preserved and monotonic (`WebhookDeliveryAttempts` reflect attempts 1, 2, 3, 4, 5, 6...).
- Replay batch operations with shared correlation keys work cleanly without cross-delivery collisions.

### Negative / Trade-offs
- An additional `RetryCycleAttemptCount` and `RetryCycleStartedAtUtc` column added to `WebhookDeliveries`. Schema migration successfully applied.
