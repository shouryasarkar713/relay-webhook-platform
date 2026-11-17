# ADR-0012: Durable Webhook Retry Policy and HTTP Failure Classification

- **Status**: Accepted
- **Date**: 2026-11-17

## 1. Context

Outbound webhook deliveries frequently encounter transient network hiccups, server restarts, load shedding, and temporary upstream outages. Without an intelligent retry mechanism, transient failures immediately drop deliveries or burden producers with manual resubmissions. Conversely, naively retrying all failures (including permanent client errors such as `400 Bad Request` or `404 Not Found`) consumes network resources, causes synchronized thundering herds against struggling endpoints, and wastes worker throughput.

Furthermore, retrying in-memory via `Task.Delay` is fragile: if a worker crashes or restarts, all scheduled retries are permanently lost.

## 2. Decision

We implemented a durable, database-backed retry engine combined with strict HTTP failure classification:

1. **Failure Classification (`IWebhookDeliveryFailureClassifier`)**:
   - **Success (2xx)**: `200 OK`, `201 Created`, `204 No Content` mark delivery as `Delivered`.
   - **Permanent Failure (3xx, non-retryable 4xx)**:
     - Redirects (3xx) are permanently blocked (`AllowAutoRedirect = false`).
     - Client errors (`400 Bad Request`, `401 Unauthorized`, `403 Forbidden`, `404 Not Found`, `409 Conflict`, `410 Gone`, `422 Unprocessable Entity`) indicate payload or configuration bugs on the client endpoint. Retrying will never succeed; the delivery transitions directly to `Failed` without consuming retry attempts.
     - Security violations (SSRF blocks, TLS certificate errors) transition directly to `Failed`.
   - **Retryable HTTP Failures**:
     - `408 Request Timeout`, `425 Too Early`, `429 Too Many Requests`.
     - All 5xx server errors (`500 Internal Server Error`, `502 Bad Gateway`, `503 Service Unavailable`, `504 Gateway Timeout`).
     - Transient network exceptions: connection resets, connection refused, socket timeouts (`SocketError`).

2. **Durable Full Jitter Exponential Backoff (`IWebhookRetryPolicy`)**:
   - Exponential formula: `ExponentialCap = min(MaxDelaySeconds, BaseDelaySeconds * 2^(attempt - 1))`.
   - Full Jitter distribution: `Delay = UniformRandom(0, ExponentialCap)`. Full Jitter spreads retry attempts uniformly across time, preventing synchronized thundering herd spikes against the recipient endpoint.
   - Default configuration: `BaseDelay = 5s`, `MaxDelay = 3600s (1h)`, `MaxAttempts = 8`.

3. **Maximum Retry Window (24-Hour Horizon)**:
   - Even if attempt budget remains, retries cannot be scheduled beyond `CreatedAtUtc + MaxRetryWindowHours` (default 24 hours).
   - If `NextAttemptAtUtc > CreatedAtUtc + 24h`, the delivery is classified as expired and transitioned to `DeadLettered`.

4. **Durable Database Scheduling**:
   - Retries are never held in-memory via `Task.Delay`.
   - Next retry timestamp is persisted atomically in SQL Server column `WebhookDeliveries.NextAttemptAtUtc`.
   - The worker's claim query filters `WHERE (Status = 'Pending' AND NextAttemptAtUtc <= SYSUTCDATETIME())`, ensuring that worker restarts, scaling events, and crash recoveries seamlessly preserve scheduled retries.

5. **Strict Attempt Counting**:
   - Background worker claiming increments `ClaimCount` (lease tracking) but **never consumes the HTTP retry budget**.
   - `AttemptCount` is incremented atomically inside SQL Server only when the worker commits `BeginDeliveryAttemptAsync` immediately prior to issuing outbound HTTP I/O.

## 3. Consequences

### Positive
- Resilience to transient outages with zero loss of scheduled retries across worker restarts.
- Full Jitter avoids thundering herds against client endpoints.
- Permanent client errors fail fast without consuming attempt budget or worker resources.
- Strict separation between lease claims and actual HTTP attempts ensures crashing workers do not deplete retry budgets.

### Negative / Trade-offs
- Requires database roundtrips to schedule each retry timestamp, mitigated by composite SQL index `(Status, NextAttemptAtUtc)`.
