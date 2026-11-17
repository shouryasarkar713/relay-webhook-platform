# ADR-0017: Circuit Breaker Probe Leases, Crash Recovery, and Stale Probe Fencing

- **Status**: Accepted
- **Date**: 2026-11-17

## 1. Context

In Phase 4, Relay's SQL Server-backed circuit breaker tracked probe state using a boolean flag `CircuitHalfOpenProbeActive` on the `WebhookEndpoints` entity. When a circuit transitioned from `Open` to `HalfOpen`, the first worker claimed the probe by setting `CircuitHalfOpenProbeActive = true`.

### The Worker Crash Vulnerability
If the worker process crashed, experienced an unhandled OOM, was killed by Kubernetes/container orchestrator, or lost network connectivity while running the probe:
1. `CircuitHalfOpenProbeActive` remained `true` indefinitely in SQL Server.
2. Subsequent deliveries checked `CircuitHalfOpenProbeActive == true` and deferred continuously.
3. The circuit breaker became **permanently wedged in HalfOpen**, unable to ever test recovery or return to `Closed`.

### The Concurrency Race Vulnerability
If multiple workers concurrently inspected an `Open` endpoint whose open duration had expired, uncoordinated reads could allow multiple workers to fire probes simultaneously, defeating the single-probe guarantee and flooding recovering endpoints.

## 2. Decision

We replaced the boolean probe flag with **Fenced Probe Leases** backed by atomic SQL updates:

1. **Probe Lease Columns on `WebhookEndpoints`**:
   - `HalfOpenProbeToken` (`Guid?`): Unique identifier for the specific probe attempt.
   - `HalfOpenProbeLeaseExpiresAtUtc` (`datetimeoffset?`): Absolute expiration timestamp for the probe lease (default duration: 30 seconds).

2. **Atomic SQL Probe Lease Acquisition (`CanExecuteDecisionAsync`)**:
   - Workers attempt to claim or transition to `HalfOpen` using an atomic conditional SQL `UPDATE`:
     ```sql
     UPDATE dbo.WebhookEndpoints
     SET CircuitState = 'HalfOpen',
         CircuitHalfOpenProbeActive = 1,
         HalfOpenProbeToken = @ProbeToken,
         HalfOpenProbeLeaseExpiresAtUtc = @ProbeExpiresAt,
         UpdatedAtUtc = @Now
     WHERE Id = @EndpointId AND (
         (CircuitState = 'HalfOpen' AND (HalfOpenProbeToken IS NULL OR HalfOpenProbeLeaseExpiresAtUtc IS NULL OR HalfOpenProbeLeaseExpiresAtUtc <= @Now))
         OR
         (CircuitState = 'Open' AND CircuitOpenedAtUtc IS NOT NULL AND DATEADD(second, @OpenDurationSeconds, CircuitOpenedAtUtc) <= @Now)
     )
     ```
   - In SQL Server, row-level locks guarantee that if multiple workers attempt to claim the probe at the exact same millisecond, exactly **one** receives `rowsAffected == 1`, while all others receive `rowsAffected == 0`.

3. **Automatic Crash Recovery**:
   - If Worker 1 claims a probe lease and dies, its probe token expires after `ProbeLeaseDurationSeconds` (30s).
   - Once `now >= HalfOpenProbeLeaseExpiresAtUtc`, the probe lease is available again.
   - Surviving Worker 2 atomically reclaims the probe lease with a new `ProbeToken` and executes the probe without operator intervention.

4. **Stale Probe Fencing**:
   - `RecordSuccessAsync(endpointId, probeToken)` and `RecordFailureAsync(endpointId, probeToken)` enforce fencing in SQL:
     ```sql
     UPDATE dbo.WebhookEndpoints
     SET CircuitState = 'Closed', ...
     WHERE Id = @EndpointId AND (CircuitState = 'HalfOpen' AND HalfOpenProbeToken = @ProbeToken)
     ```
   - If Worker 1 suffered a 40-second network stall, its lease was reclaimed by Worker 2. When Worker 1 finally completes its delayed HTTP call, its SQL update matches 0 rows because `HalfOpenProbeToken` has changed. Stale probe outcomes are safely rejected and logged.

## 3. Consequences

### Positive
- Self-healing circuit breaker: worker node termination can never wedge an endpoint in `HalfOpen`.
- Strong concurrency fencing: stale probe completions cannot corrupt endpoint state.
- Distributed safety across multi-worker deployments without introducing Redis, ZooKeeper, or external consensus systems.
- Zero probe storms against recovering customer systems.

### Negative / Trade-offs
- Probe recovery incurs a maximum latency of `ProbeLeaseDurationSeconds` (30s) if a worker crashes mid-probe. This trade-off is standard for lease-based distributed consensus.
