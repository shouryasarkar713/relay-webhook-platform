# ADR-0016: Bulkhead Admission Control, Inverted Semaphore Acquisition, and Memory Lifecycle

- **Status**: Accepted
- **Date**: 2026-11-17

## 1. Context

In Phase 4, `EndpointBulkheadManager` implemented a dual-barrier concurrency limiter using two `SemaphoreSlim` barriers:
1. A global semaphore limiting total concurrent outbound deliveries on the worker (`MaxGlobalConcurrency = 20`).
2. A per-endpoint semaphore limiting concurrent connections to any single endpoint (`MaxPerEndpointConcurrency = 5`).

However, the acquisition order was previously:
```
1. Acquire global semaphore slot (worker capacity).
2. Acquire per-endpoint semaphore slot (endpoint concurrency).
```

### The Starvation Vulnerability
If Endpoint A experienced a sudden burst of 25 deliveries, 20 of them would immediately acquire the 20 global permits. Only 5 of those would acquire Endpoint A's per-endpoint permits and begin outbound HTTP calls. The remaining 15 deliveries for Endpoint A would block on Endpoint A's per-endpoint semaphore **while holding 15 of the 20 global permits**!
Consequently, healthy Endpoint B arriving with 1 delivery would block attempting to acquire a global permit, suffering 100% starvation despite having 0 active connections.

Furthermore, dynamic per-endpoint semaphores were cached in an unbound `ConcurrentDictionary<Guid, SemaphoreSlim>`, which constituted a slow memory leak over weeks of operation as thousands of unique endpoints were registered and decommissioned.

## 2. Decision

We overhauled `EndpointBulkheadManager` with the following architectural guarantees:

1. **Inverted Acquisition Order (Admission Control First)**:
   - Deliveries must acquire the **Per-Endpoint Semaphore FIRST** (admission control).
   - Only deliveries that successfully pass the per-endpoint limit (at most `MaxPerEndpointConcurrency = 5` for any single endpoint) are permitted to attempt acquiring the **Global Semaphore SECOND** (execution capacity).
   - If Endpoint A receives 25 deliveries, 5 pass admission control and compete for global permits; the other 20 wait on Endpoint A's queue without consuming any global permits.
   - Endpoint B can immediately pass its admission control and acquire available global slots without being starved by Endpoint A's backlog.

2. **Inverted Release Order**:
   - On release (`BulkheadLease.Dispose`), permits are released in reverse order:
     1. Global permit released first (immediately unblocking waiting workers on other endpoints).
     2. Per-endpoint permit released second.

3. **Safe Memory Lifecycle and Sweeping (`EndpointSemaphoreHolder`)**:
   - Each endpoint semaphore is encapsulated within an `EndpointSemaphoreHolder` maintaining:
     - The `SemaphoreSlim` instance.
     - An atomic `ActiveCount` (reference count).
     - A `LastUsedAtUtc` timestamp.
   - Added `SweepInactiveEndpoints(idleTimeout)`:
     - Scans registered holders.
     - When `ActiveCount == 0` and `now - LastUsedAtUtc > idleTimeout` (default 10 minutes), the holder is safely removed via atomic CAS (`TryRemove(KeyValuePair)`) and disposed.
     - Active semaphores (`ActiveCount > 0`) are guaranteed never to be swept or disposed while in use.

4. **Process-Local Scope Clarification**:
   - Concurrency bulkheads are explicitly documented as **process-local**. Each worker instance enforces its own configured limits. When scaling to $N$ worker replicas, the cluster-wide concurrency ceiling scales linearly to $N \times \text{MaxConcurrency}$, isolating process-level resources (thread pool, TCP sockets) without cross-network synchronization overhead.

## 3. Consequences

### Positive
- Strict noisy-neighbor isolation: an endpoint burst can never monopolize global worker capacity.
- Zero starvation: healthy endpoints acquire execution slots with bounded, predictable latency.
- Bounded memory footprint: idle endpoint semaphores are systematically reclaimed.
- Lock-free, low-latency semaphore acquisition without external coordinator or Redis latency.

### Negative / Trade-offs
- Cluster-wide concurrency is bounded by $N \times \text{Limit}$ rather than a single global distributed limit. For outbound webhooks, process-level socket isolation is standard practice and avoids distributed lock bottlenecks.
