# ADR-0006: Outbox Dispatcher and Lease-Based Concurrent Batch Claiming

- **Status**: Accepted
- **Date**: 2026-11-17

## 1. Context

Relay ingests high volumes of webhook events durably via SQL Server. As part of the Transactional Outbox pattern, an asynchronous background worker (`Relay.Worker`) must continuously poll `OutboxMessages`, dispatch them to Azure Service Bus, and record their publication status.

In a horizontally scaled environment with multiple `Relay.Worker` instances running simultaneously, naïve polling (`SELECT TOP 50 WHERE Status = 'Pending'`) causes concurrency race conditions:
1. Multiple workers read and publish identical outbox messages, leading to duplicated message delivery downstream.
2. Naïve table-level locking degrades throughput on the ingestion path.
3. If a worker crashes or encounters an execution stall midway through publishing, messages could remain permanently locked or lost.

## 2. Problem

How can multiple distributed worker instances claim non-overlapping batches of outbox records concurrently from SQL Server with zero duplicate claiming, automatic crash recovery for crashed or stalled nodes, and strict fencing against stale workers?

## 3. Decision

We implemented a **Lease-Based Atomic Batch Claiming** mechanism backed by SQL Server's table hints (`UPDLOCK`, `READPAST`, `ROWLOCK`, `READCOMMITTEDLOCK`) inside a Common Table Expression (CTE):

```sql
;WITH Candidates AS (
    SELECT TOP (@BatchSize) *
    FROM dbo.OutboxMessages WITH (UPDLOCK, READPAST, ROWLOCK, READCOMMITTEDLOCK)
    WHERE (Status = 'Pending' AND NextAttemptAtUtc <= @Now)
       OR (Status = 'Processing' AND LeaseExpiresAtUtc <= @Now)
    ORDER BY CreatedAtUtc, Id
)
UPDATE Candidates
SET Status = 'Processing',
    LeaseToken = @LeaseToken,
    LeaseExpiresAtUtc = @LeaseExpiry,
    LastAttemptAtUtc = @Now,
    ClaimCount = ClaimCount + 1
OUTPUT inserted.*;
```

### Key Elements of the Design:

1. **Short SQL Claim Transaction**:
   The SQL claim operation is executed as a short, focused transaction. Once claimed, the transaction commits immediately. The worker now owns the records until `LeaseExpiresAtUtc`.

2. **Network I/O Outside Database Transactions**:
   Azure Service Bus message publishing is performed **outside** any SQL transaction. Database transactions must never span external network hops.

3. **Optimistic Fencing via LeaseToken**:
   When updating record state (`MarkPublished`, `ScheduleRetry`, `MarkFailed`), workers specify `WHERE Id = @Id AND LeaseToken = @LeaseToken`. If a worker encounters an unexpected latency spike and its lease expires, another worker will reclaim the message. When the stale worker eventually attempts to update the database, `RowsAffected == 0`, safely fencing the stale worker and preventing zombie state overrides.

4. **Crash Recovery and Counter Semantics**:
   - `ClaimCount`: Tracks lease acquisitions. Worker crashes during/before network I/O increment `ClaimCount` without consuming the publication retry budget.
   - `PublishAttemptCount`: Tracks actual network publication attempts. Incrementing occurs only when network dispatch is actually attempted or failed.
   - Abandoned or timed-out messages automatically become eligible for reclaiming by any healthy worker when `LeaseExpiresAtUtc <= @Now`.

5. **SQL Server Isolation and RCSI Compatibility**:
   In environments where `READ_COMMITTED_SNAPSHOT` (RCSI) is enabled, readers default to row versioning rather than taking shared locks. The `READCOMMITTEDLOCK` hint forces the statement to acquire locking read semantics so that `UPDLOCK` and `READPAST` acquire and respect row-level locks as intended.

6. **Conventional Full Jitter Exponential Backoff**:
   When transient network errors occur during publishing, `NextAttemptAtUtc` is scheduled using conventional full jitter:
   $$\text{ExponentialCap} = \min(\text{MaxDelay}, \text{BaseDelay} \times 2^{\text{attempt}-1})$$
   $$\text{ActualDelay} = \text{UniformRandom}(0, \text{ExponentialCap})$$
   This prevents synchronized retry stampedes ("thundering herd") against the message broker.

## 4. Consequences & Trade-offs

### Positive
- Minimizes lock contention: `READPAST` skips rows currently locked by other workers.
- Zero duplicate batch claims across horizontal worker replicas.
- Resilient to worker crashes with automatic lease expiration and reclaim.
- Fencing prevents stale workers from corrupting state after lease expiry.
- Clear separation between lease claim counts and actual broker publication attempts.

### Negative / Trade-offs & Realistic Limitations
- `READPAST` and `ROWLOCK` significantly reduce lock contention, but do not provide an absolute mathematical guarantee against all possible deadlocks or blocking (e.g., page-level lock escalation if batch size is excessively large or if non-clustered index maintenance acquires locks).
- If a worker publishes a message to Azure Service Bus and crashes before updating SQL Server, the message will be reclaimed by another worker after lease expiration and published again. Downstream consumers must handle at-least-once delivery semantics using the stable `EventId` as the deduplication identifier.
