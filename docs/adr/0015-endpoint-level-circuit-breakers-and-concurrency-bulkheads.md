# ADR-0015: Endpoint-Level Circuit Breakers and Concurrency Bulkheads

- **Status**: Accepted
- **Date**: 2026-11-17

## 1. Context

In a multi-tenant webhook engine, one malfunctioning or unresponsive endpoint must not degrade performance for other healthy endpoints (noisy neighbor problem). Two specific failure modes arise:
1. **Slow / Hanging Endpoints Consuming Worker Threads**: If an endpoint takes 10 seconds to respond and receives hundreds of events, worker threads become saturated waiting for HTTP socket I/O, starving healthy endpoints of delivery capacity.
2. **Cascading Failure & Thundering Herds against Failing Services**: If an endpoint has crashed, firing hundreds of concurrent webhook requests wastes CPU, database connections, and egress bandwidth, while compounding upstream recovery issues.

## 2. Decision

We implemented a two-tiered resilience architecture combining **Endpoint Circuit Breakers** and **Concurrency Bulkheads**:

1. **Shared SQL Server-Backed Circuit Breakers (`IEndpointCircuitBreakerManager`)**:
   - Rather than storing circuit breaker state in in-memory singletons or introducing Redis dependencies, Relay tracks circuit state directly on the `WebhookEndpoint` entity in SQL Server (`CircuitState`, `ConsecutiveFailureCount`, `CircuitOpenedAtUtc`, `CircuitHalfOpenProbeActive`).
   - State Transitions:
     - **Closed**: Default state. All events dispatch normally.
     - **Open**: When consecutive delivery failures reach `FailureThreshold` (default 5), the circuit trips to `Open` and records `CircuitOpenedAtUtc`.
     - **HalfOpen**: After `OpenDurationSeconds` (default 60s) has elapsed since tripping, the circuit transitions to `HalfOpen`.
     - **Single Half-Open Probe**: Only **one** delivery is allowed to probe the endpoint (`TryAcquireHalfOpenProbe`). All other deliveries remain deferred.
       - If the probe succeeds: Circuit resets to `Closed`, failure count resets to 0.
       - If the probe fails: Circuit trips immediately back to `Open` for another duration window.
   - **Deferred Delivery Semantics (`DeferForCircuitBreakerAsync`)**:
     - When a delivery's target endpoint is `Open` or actively probing, the worker defers the delivery by setting `NextAttemptAtUtc = now + OpenDuration`.
     - The delivery remains `Pending`.
     - **Crucially, no HTTP request is made and `AttemptCount` is NOT incremented.** The client's retry budget is never consumed by circuit deferrals.

2. **Dual-Level Concurrency Bulkheads (`IBulkheadManager`, `EndpointBulkheadManager`)**:
   - Implements resource isolation using dual `SemaphoreSlim` barriers:
     - **Global Worker Concurrency Barrier (`MaxGlobalConcurrency`, default 20)**: Limits total concurrent outbound HTTP sockets dispatched across the entire worker node, preventing thread pool and OS socket descriptor exhaustion.
     - **Per-Endpoint Concurrency Barrier (`MaxPerEndpointConcurrency`, default 5)**: Limits concurrent HTTP socket connections to any single endpoint ID.
   - Any endpoint with a surge in traffic or high latency can consume at most 5 concurrent connections, guaranteeing that 15 slots remain available for all other endpoints.
   - Leases implement `IDisposable`, guaranteeing immediate slot release upon request completion, cancellation, or exception.

## 3. Consequences

### Positive
- Strict fault isolation: slow, unresponsive, or crashing endpoints cannot starve healthy tenant deliveries.
- Distributed circuit breaker state shared across all worker replicas via SQL Server without requiring Redis or distributed cache infrastructure.
- Zero penalty to tenant retry budgets during circuit outages: deliveries are paused, not burned.
- Single probe mechanics prevent overwhelming recovering endpoints.

### Negative / Trade-offs
- Endpoint updates on state changes require SQL Server writes, mitigated by short atomic transactions.
