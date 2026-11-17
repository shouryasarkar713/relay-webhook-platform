# ADR-0013: Retry-After Handling and Server-Directed Retry Scheduling

- **Status**: Accepted
- **Date**: 2026-11-17

## 1. Context

When webhook endpoints experience severe load or rate limiting, they frequently return `429 Too Many Requests` or `503 Service Unavailable` accompanied by a standard HTTP `Retry-After` response header (RFC 7231 / RFC 9110).

Ignoring `Retry-After` causes Relay to retry prematurely according to its own local backoff schedule, exacerbating endpoint overload and violating upstream contracts. However, blindly trusting unvalidated server headers introduces denial-of-service risks: a malicious or misconfigured server could return a date years in the future (e.g. `Retry-After: 3153600000`), permanently stranding delivery records in SQL Server.

## 2. Decision

We implemented a safe, standards-compliant server-directed retry scheduling engine:

1. **Parser Implementation (`IRetryAfterParser`, `RetryAfterParser`)**:
   - Supports both RFC-compliant representations:
     - **Delta-seconds**: Positive integer representing delay in seconds (e.g. `Retry-After: 120`).
     - **HTTP-date**: RFC1123 GMT timestamp (e.g. `Retry-After: Wed, 21 Oct 2026 07:28:00 GMT`).
   - Parses date values strictly in UTC using injectable `TimeProvider`.
   - Rejects negative integers, malformed strings, or dates in the past (treated as zero-delay or null).

2. **Server-Directed Delay Constraints**:
   - **Never Retry Earlier**: When a valid `Retry-After` is parsed, Relay guarantees `Delay = max(CalculatedJitterDelay, ServerRequestedDelay)`. The system will never retry earlier than the endpoint requested.
   - **Maximum Horizon Clamping (`MaxServerRequestedDelaySeconds`)**:
     - Configurable upper ceiling (default 86,400s / 24 hours).
     - If the server requests a delay exceeding this horizon (e.g., `Retry-After: 1000000`), the parser flags `ExceedsMaxHorizon = true`.
     - The retry policy evaluates this as an unreasonable upstream requirement and transitions the delivery to `DeadLettered` with reason: `Server-requested Retry-After ({requestedSeconds}s) exceeds maximum allowed horizon ({maxHorizon}s)`.

3. **Rate Limiting vs Circuit Breakers**:
   - HTTP `429 Too Many Requests` indicates cooperative rate limiting, not endpoint downtime or service crashes.
   - Therefore, `429` errors are classified as `CountsTowardCircuitBreaker = false`. An endpoint that rate limits via `429` will not trigger a circuit breaker outage for other events.

## 3. Consequences

### Positive
- Strict adherence to RFC 7231/9110 `Retry-After` conventions.
- Respects customer rate limits without hammering struggling services.
- Immune to integer overflow and arbitrary far-future dates that could strand deliveries.
- Differentiates rate limiting (429) from hard failures (500/503), preventing improper circuit tripping.

### Negative / Trade-offs
- Endpoints with misconfigured long delays are terminated into DeadLettered status, requiring manual inspection and replay.
