using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Relay.Application.Abstractions;
using Relay.Application.Exceptions;
using Relay.Domain.Entities;
using Relay.Domain.Enums;

namespace Relay.Infrastructure.Persistence;

public sealed class WebhookDeliveryStore : IWebhookDeliveryStore
{
    private readonly RelayDbContext _dbContext;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WebhookDeliveryStore> _logger;

    public WebhookDeliveryStore(
        RelayDbContext dbContext,
        TimeProvider timeProvider,
        ILogger<WebhookDeliveryStore> logger)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<IReadOnlyList<WebhookDelivery>> ClaimPendingDeliveriesAsync(
        int batchSize,
        Guid leaseToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize), "Batch size must be greater than zero.");
        }

        var now = _timeProvider.GetUtcNow();
        var leaseExpiry = now.Add(leaseDuration);

        // Atomic claim CTE using UPDLOCK, READPAST, ROWLOCK, READCOMMITTEDLOCK
        // Filters on NextAttemptAtUtc to ensure future-scheduled retries are NOT claimed prematurely
        const string claimSql = """
            ;WITH Candidates AS (
                SELECT TOP (@BatchSize) *
                FROM dbo.WebhookDeliveries WITH (UPDLOCK, READPAST, ROWLOCK, READCOMMITTEDLOCK)
                WHERE (Status = 'Pending' AND NextAttemptAtUtc <= @Now)
                   OR (Status = 'Processing' AND LeaseExpiresAtUtc <= @Now)
                ORDER BY CreatedAtUtc, Id
            )
            UPDATE Candidates
            SET Status = 'Processing',
                LeaseToken = @LeaseToken,
                LeaseExpiresAtUtc = @LeaseExpiry,
                ClaimCount = ClaimCount + 1
            OUTPUT inserted.Id,
                   inserted.EventId,
                   inserted.EndpointId,
                   inserted.TenantId,
                   inserted.Status,
                   inserted.CreatedAtUtc,
                   inserted.LastAttemptAtUtc,
                   inserted.NextAttemptAtUtc,
                   inserted.DeliveredAtUtc,
                   inserted.DeadLetteredAtUtc,
                   inserted.ClaimCount,
                   inserted.AttemptCount,
                   inserted.RetryCycleAttemptCount,
                   inserted.RetryCycleStartedAtUtc,
                   inserted.ReplayCount,
                   inserted.LastHttpStatusCode,
                   inserted.LastErrorCategory,
                   inserted.LastError,
                   inserted.LeaseToken,
                   inserted.LeaseExpiresAtUtc;
            """;

        var batchSizeParam = new SqlParameter("@BatchSize", batchSize);
        var nowParam = new SqlParameter("@Now", now);
        var leaseTokenParam = new SqlParameter("@LeaseToken", leaseToken);
        var leaseExpiryParam = new SqlParameter("@LeaseExpiry", leaseExpiry);

        var claimedDeliveries = await _dbContext.WebhookDeliveries
            .FromSqlRaw(claimSql, batchSizeParam, nowParam, leaseTokenParam, leaseExpiryParam)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        if (claimedDeliveries.Count == 0)
        {
            return claimedDeliveries;
        }

        var deliveryIds = claimedDeliveries.Select(d => d.Id).ToList();

        // Check for any incomplete "Started" attempts from prior crashed workers whose lease expired
        var orphanAttempts = await _dbContext.WebhookDeliveryAttempts
            .Where(a => deliveryIds.Contains(a.DeliveryId) && a.Outcome == DeliveryAttemptOutcome.Started)
            .ToListAsync(cancellationToken);

        if (orphanAttempts.Count > 0)
        {
            var orphanIds = orphanAttempts.Select(a => a.Id).ToList();
            await _dbContext.WebhookDeliveryAttempts
                .Where(a => orphanIds.Contains(a.Id))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.Outcome, DeliveryAttemptOutcome.WorkerInterrupted)
                    .SetProperty(a => a.CompletedAtUtc, now)
                    .SetProperty(a => a.ErrorCategory, "WorkerCrashedOrLeaseExpired")
                    .SetProperty(a => a.ResponseExcerpt, "Attempt abandoned due to worker crash or lease expiration"),
                    cancellationToken);
        }

        // Hydrate navigations
        var hydrated = await _dbContext.WebhookDeliveries
            .Where(d => deliveryIds.Contains(d.Id))
            .Include(d => d.Endpoint)
            .Include(d => d.Event)
            .Include(d => d.Attempts)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return hydrated;
    }

    public async Task<bool> BeginDeliveryAttemptAsync(
        Guid deliveryId,
        Guid leaseToken,
        WebhookDeliveryAttempt initialAttempt,
        CancellationToken cancellationToken = default)
    {
        var startedAt = initialAttempt.StartedAtUtc;

        const string updateAttemptCountSql = """
            UPDATE dbo.WebhookDeliveries
            SET AttemptCount = AttemptCount + 1,
                RetryCycleAttemptCount = RetryCycleAttemptCount + 1,
                LastAttemptAtUtc = @StartedAt
            WHERE Id = @Id AND LeaseToken = @LeaseToken;
            """;

        var idParam = new SqlParameter("@Id", deliveryId);
        var leaseTokenParam = new SqlParameter("@LeaseToken", leaseToken);
        var startedAtParam = new SqlParameter("@StartedAt", startedAt);

        var strategy = _dbContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

            var rowsAffected = await _dbContext.Database.ExecuteSqlRawAsync(
                updateAttemptCountSql,
                new object[] { idParam, leaseTokenParam, startedAtParam },
                cancellationToken);

            if (rowsAffected == 0)
            {
                _logger.LogWarning(
                    "Fencing blocked BeginDeliveryAttempt for WebhookDelivery {DeliveryId}: lease token mismatch or expired.",
                    deliveryId);
                await tx.RollbackAsync(cancellationToken);
                return false;
            }

            // Persist the durable attempt record in Started state before network I/O
            _dbContext.WebhookDeliveryAttempts.Add(initialAttempt);
            await _dbContext.SaveChangesAsync(cancellationToken);
            _dbContext.Entry(initialAttempt).State = EntityState.Detached;

            await tx.CommitAsync(cancellationToken);
            return true;
        });
    }

    public async Task<bool> CompleteDeliveryAttemptAsync(
        Guid deliveryId,
        Guid leaseToken,
        WebhookDeliveryAttempt completedAttempt,
        WebhookDeliveryStatus targetStatus,
        DateTimeOffset? nextAttemptAtUtc,
        DateTimeOffset? deadLetteredAtUtc,
        string? errorCategory,
        string? errorMessage,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        const string updateDeliverySql = """
            UPDATE dbo.WebhookDeliveries
            SET Status = @Status,
                DeliveredAtUtc = CASE WHEN @Status = 'Delivered' THEN @Now ELSE DeliveredAtUtc END,
                DeadLetteredAtUtc = @DeadLetteredAtUtc,
                NextAttemptAtUtc = ISNULL(@NextAttemptAtUtc, NextAttemptAtUtc),
                LastAttemptAtUtc = @Now,
                LastHttpStatusCode = @HttpStatusCode,
                LastErrorCategory = @ErrorCategory,
                LastError = @LastError,
                LeaseToken = NULL,
                LeaseExpiresAtUtc = NULL
            WHERE Id = @Id AND LeaseToken = @LeaseToken;
            """;

        var idParam = new SqlParameter("@Id", deliveryId);
        var leaseTokenParam = new SqlParameter("@LeaseToken", leaseToken);
        var statusParam = new SqlParameter("@Status", targetStatus.ToString());
        var nowParam = new SqlParameter("@Now", now);
        var deadLetterParam = new SqlParameter("@DeadLetteredAtUtc", (object?)deadLetteredAtUtc ?? DBNull.Value);
        var nextAttemptParam = new SqlParameter("@NextAttemptAtUtc", (object?)nextAttemptAtUtc ?? DBNull.Value);
        var httpCodeParam = new SqlParameter("@HttpStatusCode", (object?)completedAttempt.HttpStatusCode ?? DBNull.Value);
        var errorCatParam = new SqlParameter("@ErrorCategory", (object?)errorCategory ?? DBNull.Value);
        var errorParam = new SqlParameter("@LastError", (object?)errorMessage ?? DBNull.Value);

        const string updateAttemptSql = """
            UPDATE dbo.WebhookDeliveryAttempts
            SET CompletedAtUtc = @CompletedAtUtc,
                DurationMilliseconds = @DurationMs,
                HttpStatusCode = @HttpStatusCode,
                Outcome = @Outcome,
                ErrorCategory = @ErrorCategory,
                ResponseExcerpt = @ResponseExcerpt
            WHERE Id = @AttemptId;
            """;

        var attemptIdParam = new SqlParameter("@AttemptId", completedAttempt.Id);
        var attemptCompletedParam = new SqlParameter("@CompletedAtUtc", completedAttempt.CompletedAtUtc);
        var attemptDurationParam = new SqlParameter("@DurationMs", completedAttempt.DurationMilliseconds);
        var attemptHttpCodeParam = new SqlParameter("@HttpStatusCode", (object?)completedAttempt.HttpStatusCode ?? DBNull.Value);
        var attemptOutcomeParam = new SqlParameter("@Outcome", completedAttempt.Outcome.ToString());
        var attemptErrorCatParam = new SqlParameter("@ErrorCategory", (object?)completedAttempt.ErrorCategory ?? DBNull.Value);
        var attemptExcerptParam = new SqlParameter("@ResponseExcerpt", (object?)completedAttempt.ResponseExcerpt ?? DBNull.Value);

        var strategy = _dbContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

            var rowsAffected = await _dbContext.Database.ExecuteSqlRawAsync(
                updateDeliverySql,
                new object[] { idParam, leaseTokenParam, statusParam, nowParam, deadLetterParam, nextAttemptParam, httpCodeParam, errorCatParam, errorParam },
                cancellationToken);

            if (rowsAffected == 0)
            {
                _logger.LogWarning(
                    "Fencing blocked CompleteDeliveryAttempt for WebhookDelivery {DeliveryId}: lease token {LeaseToken} expired or reclaimed.",
                    deliveryId,
                    leaseToken);
                await tx.RollbackAsync(cancellationToken);
                return false;
            }

            // Update the attempt row with completed results
            await _dbContext.Database.ExecuteSqlRawAsync(
                updateAttemptSql,
                new object[] { attemptIdParam, attemptCompletedParam, attemptDurationParam, attemptHttpCodeParam, attemptOutcomeParam, attemptErrorCatParam, attemptExcerptParam },
                cancellationToken);

            await tx.CommitAsync(cancellationToken);
            return true;
        });
    }

    public async Task<bool> DeferDeliveryForCircuitBreakerAsync(
        Guid deliveryId,
        Guid leaseToken,
        DateTimeOffset nextAttemptAtUtc,
        string reason,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE dbo.WebhookDeliveries
            SET Status = 'Pending',
                NextAttemptAtUtc = @NextAttemptAtUtc,
                LastErrorCategory = 'CircuitBreakerDeferred',
                LastError = @Reason,
                LeaseToken = NULL,
                LeaseExpiresAtUtc = NULL
            WHERE Id = @Id AND LeaseToken = @LeaseToken;
            """;

        var idParam = new SqlParameter("@Id", deliveryId);
        var leaseTokenParam = new SqlParameter("@LeaseToken", leaseToken);
        var nextAttemptParam = new SqlParameter("@NextAttemptAtUtc", nextAttemptAtUtc);
        var reasonParam = new SqlParameter("@Reason", reason);

        var rowsAffected = await _dbContext.Database.ExecuteSqlRawAsync(
            sql,
            new object[] { idParam, leaseTokenParam, nextAttemptParam, reasonParam },
            cancellationToken);

        return rowsAffected > 0;
    }

    public async Task<WebhookDeliveryReplayAudit> ReplayDeliveryAsync(
        Guid deliveryId,
        Guid tenantId,
        string? replayKey,
        string requestedBy,
        DateTimeOffset replayedAtUtc,
        CancellationToken cancellationToken = default)
    {
        // 1. Idempotency check: if replayKey is supplied and already recorded for this tenant + delivery, return it
        if (!string.IsNullOrWhiteSpace(replayKey))
        {
            var existingAudit = await _dbContext.WebhookDeliveryReplayAudits
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    r => r.TenantId == tenantId && r.DeliveryId == deliveryId && r.ReplayKey == replayKey.Trim(),
                    cancellationToken);

            if (existingAudit is not null)
            {
                return existingAudit;
            }
        }

        // 2. Fetch delivery scoped to tenant
        var delivery = await _dbContext.WebhookDeliveries
            .FirstOrDefaultAsync(d => d.Id == deliveryId && d.TenantId == tenantId, cancellationToken);

        if (delivery is null)
        {
            throw new ResourceNotFoundException("WebhookDelivery", deliveryId);
        }

        // 3. Verify endpoint is still active and valid
        var endpoint = await _dbContext.WebhookEndpoints
            .FirstOrDefaultAsync(e => e.Id == delivery.EndpointId && e.TenantId == tenantId, cancellationToken);

        if (endpoint is null || !endpoint.IsActive)
        {
            throw new InvalidOperationException($"Cannot replay delivery: target endpoint '{delivery.EndpointId}' is deactivated or does not exist.");
        }

        var previousStatus = delivery.Status;

        // 4. Perform domain Replay state transition (validates status is DeadLettered or Failed)
        delivery.Replay(replayedAtUtc);

        var audit = new WebhookDeliveryReplayAudit(
            Guid.NewGuid(),
            tenantId,
            deliveryId,
            replayKey?.Trim(),
            requestedBy,
            previousStatus,
            replayedAtUtc);

        _dbContext.WebhookDeliveryReplayAudits.Add(audit);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (!string.IsNullOrWhiteSpace(replayKey))
        {
            _logger.LogInformation(
                ex,
                "Concurrent replay detected for Delivery {DeliveryId} with key {ReplayKey}. Returning existing audit.",
                deliveryId,
                replayKey);

            var existingAudit = await _dbContext.WebhookDeliveryReplayAudits
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    r => r.TenantId == tenantId && r.DeliveryId == deliveryId && r.ReplayKey == replayKey.Trim(),
                    cancellationToken);

            if (existingAudit is not null)
            {
                return existingAudit;
            }

            throw;
        }

        _logger.LogInformation(
            "WebhookDelivery {DeliveryId} replayed by {RequestedBy} (previous status: {PreviousStatus})",
            deliveryId,
            requestedBy,
            previousStatus);

        return audit;
    }
}
