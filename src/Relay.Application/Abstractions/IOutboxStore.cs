using Relay.Domain.Entities;

namespace Relay.Application.Abstractions;

public interface IOutboxStore
{
    Task<IReadOnlyList<OutboxMessage>> ClaimBatchAsync(
        int batchSize,
        Guid leaseToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    Task MarkPublishedAsync(
        Guid id,
        Guid leaseToken,
        DateTimeOffset publishedAtUtc,
        CancellationToken cancellationToken = default);

    Task ScheduleRetryAsync(
        Guid id,
        Guid leaseToken,
        DateTimeOffset nextAttemptUtc,
        string error,
        CancellationToken cancellationToken = default);

    Task MarkFailedAsync(
        Guid id,
        Guid leaseToken,
        string error,
        CancellationToken cancellationToken = default);
}
