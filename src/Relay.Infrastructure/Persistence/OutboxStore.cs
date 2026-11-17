using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Relay.Application.Abstractions;
using Relay.Domain.Entities;
using Relay.Domain.Enums;
using Relay.Infrastructure.Data;

namespace Relay.Infrastructure.Persistence;

public sealed class OutboxStore : IOutboxStore
{
    private readonly RelayDbContext _dbContext;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OutboxStore> _logger;

    public OutboxStore(
        RelayDbContext dbContext,
        TimeProvider timeProvider,
        ILogger<OutboxStore> logger)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<IReadOnlyList<OutboxMessage>> ClaimBatchAsync(
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
        // In SQL Server with READ_COMMITTED_SNAPSHOT ON (RCSI), READCOMMITTEDLOCK forces statement to use
        // locking read semantics instead of snapshot row-versioning, ensuring UPDLOCK and READPAST lock candidates reliably.
        const string claimSql = """
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
            OUTPUT inserted.Id,
                   inserted.EventId,
                   inserted.TenantId,
                   inserted.MessageType,
                   inserted.PayloadJson,
                   inserted.CreatedAtUtc,
                   inserted.PublishedAtUtc,
                   inserted.ClaimCount,
                   inserted.PublishAttemptCount,
                   inserted.LastError,
                   inserted.Status,
                   inserted.LeaseToken,
                   inserted.LeaseExpiresAtUtc,
                   inserted.NextAttemptAtUtc,
                   inserted.LastAttemptAtUtc;
            """;

        var batchSizeParam = new SqlParameter("@BatchSize", batchSize);
        var nowParam = new SqlParameter("@Now", now);
        var leaseTokenParam = new SqlParameter("@LeaseToken", leaseToken);
        var leaseExpiryParam = new SqlParameter("@LeaseExpiry", leaseExpiry);

        var claimedMessages = await _dbContext.OutboxMessages
            .FromSqlRaw(claimSql, batchSizeParam, nowParam, leaseTokenParam, leaseExpiryParam)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        if (claimedMessages.Count > 0)
        {
            _logger.LogInformation(
                "Claimed batch of {Count} outbox messages under lease {LeaseToken} expiring at {LeaseExpiry}",
                claimedMessages.Count,
                leaseToken,
                leaseExpiry);
        }

        return claimedMessages;
    }

    public async Task MarkPublishedAsync(
        Guid id,
        Guid leaseToken,
        DateTimeOffset publishedAtUtc,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE dbo.OutboxMessages
            SET Status = 'Published',
                PublishAttemptCount = PublishAttemptCount + 1,
                PublishedAtUtc = @PublishedAtUtc,
                LeaseToken = NULL,
                LeaseExpiresAtUtc = NULL,
                LastError = NULL
            WHERE Id = @Id AND LeaseToken = @LeaseToken;
            """;

        var idParam = new SqlParameter("@Id", id);
        var leaseTokenParam = new SqlParameter("@LeaseToken", leaseToken);
        var publishedAtParam = new SqlParameter("@PublishedAtUtc", publishedAtUtc);

        var rowsAffected = await _dbContext.Database
            .ExecuteSqlRawAsync(sql, new object[] { idParam, leaseTokenParam, publishedAtParam }, cancellationToken);

        if (rowsAffected == 0)
        {
            _logger.LogWarning(
                "Failed to mark outbox message {Id} as Published: lease {LeaseToken} expired or message status altered",
                id,
                leaseToken);
        }
        else
        {
            _logger.LogDebug("Marked outbox message {Id} as Published", id);
        }
    }

    public async Task ScheduleRetryAsync(
        Guid id,
        Guid leaseToken,
        DateTimeOffset nextAttemptUtc,
        string error,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        const string sql = """
            UPDATE dbo.OutboxMessages
            SET Status = 'Pending',
                PublishAttemptCount = PublishAttemptCount + 1,
                NextAttemptAtUtc = @NextAttemptUtc,
                LastError = @LastError,
                LeaseToken = NULL,
                LeaseExpiresAtUtc = NULL,
                LastAttemptAtUtc = @Now
            WHERE Id = @Id AND LeaseToken = @LeaseToken;
            """;

        var idParam = new SqlParameter("@Id", id);
        var leaseTokenParam = new SqlParameter("@LeaseToken", leaseToken);
        var nextAttemptParam = new SqlParameter("@NextAttemptUtc", nextAttemptUtc);
        var errorParam = new SqlParameter("@LastError", (object?)error ?? DBNull.Value);
        var nowParam = new SqlParameter("@Now", now);

        var rowsAffected = await _dbContext.Database
            .ExecuteSqlRawAsync(sql, new object[] { idParam, leaseTokenParam, nextAttemptParam, errorParam, nowParam }, cancellationToken);

        if (rowsAffected == 0)
        {
            _logger.LogWarning(
                "Failed to schedule retry for outbox message {Id}: lease {LeaseToken} expired or message status altered",
                id,
                leaseToken);
        }
        else
        {
            _logger.LogInformation(
                "Scheduled retry for outbox message {Id} at {NextAttemptUtc}. Error: {Error}",
                id,
                nextAttemptUtc,
                error);
        }
    }

    public async Task MarkFailedAsync(
        Guid id,
        Guid leaseToken,
        string error,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        const string sql = """
            UPDATE dbo.OutboxMessages
            SET Status = 'Failed',
                PublishAttemptCount = PublishAttemptCount + 1,
                LastError = @LastError,
                LeaseToken = NULL,
                LeaseExpiresAtUtc = NULL,
                LastAttemptAtUtc = @Now
            WHERE Id = @Id AND LeaseToken = @LeaseToken;
            """;

        var idParam = new SqlParameter("@Id", id);
        var leaseTokenParam = new SqlParameter("@LeaseToken", leaseToken);
        var errorParam = new SqlParameter("@LastError", (object?)error ?? DBNull.Value);
        var nowParam = new SqlParameter("@Now", now);

        var rowsAffected = await _dbContext.Database
            .ExecuteSqlRawAsync(sql, new object[] { idParam, leaseTokenParam, errorParam, nowParam }, cancellationToken);

        if (rowsAffected == 0)
        {
            _logger.LogWarning(
                "Failed to mark outbox message {Id} as Failed: lease {LeaseToken} expired or message status altered",
                id,
                leaseToken);
        }
        else
        {
            _logger.LogError(
                "Marked outbox message {Id} as permanently Failed. Final error: {Error}",
                id,
                error);
        }
    }
}
