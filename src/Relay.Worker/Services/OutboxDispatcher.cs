using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Relay.Application.Abstractions;
using Relay.Application.Messaging;
using Relay.Domain.Entities;
using Relay.Worker.Options;
using Relay.Worker.Utilities;

namespace Relay.Worker.Services;

public sealed class OutboxDispatcher
{
    private readonly IOutboxStore _outboxStore;
    private readonly IEventMessagePublisher _publisher;
    private readonly TimeProvider _timeProvider;
    private readonly OutboxDispatcherOptions _options;
    private readonly ILogger<OutboxDispatcher> _logger;

    public OutboxDispatcher(
        IOutboxStore outboxStore,
        IEventMessagePublisher publisher,
        TimeProvider timeProvider,
        IOptions<OutboxDispatcherOptions> options,
        ILogger<OutboxDispatcher> logger)
    {
        _outboxStore = outboxStore;
        _publisher = publisher;
        _timeProvider = timeProvider;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken = default)
    {
        var leaseToken = Guid.NewGuid();

        // 1. Claim batch of eligible messages atomically
        var messages = await _outboxStore.ClaimBatchAsync(
            _options.BatchSize,
            leaseToken,
            _options.LeaseDuration,
            cancellationToken);

        if (messages.Count == 0)
        {
            return 0;
        }

        _logger.LogInformation(
            "Processing claimed batch of {Count} outbox messages under lease {LeaseToken}",
            messages.Count,
            leaseToken);

        // 2. Dispatch messages concurrently bounded by MaxConcurrency
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = _options.MaxConcurrency,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(messages, parallelOptions, async (message, ct) =>
        {
            await ProcessSingleMessageAsync(message, leaseToken, ct);
        });

        return messages.Count;
    }

    private async Task ProcessSingleMessageAsync(
        OutboxMessage message,
        Guid leaseToken,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();

        try
        {
            // Parse envelope without double serialization
            var envelope = OutboxEnvelope.Create(
                message.EventId,
                message.TenantId,
                message.MessageType,
                message.CreatedAtUtc,
                message.PayloadJson);

            // Network publish OUTSIDE SQL transaction
            await _publisher.PublishAsync(envelope, cancellationToken);

            // Mark published with lease token verification
            var publishedAt = _timeProvider.GetUtcNow();
            await _outboxStore.MarkPublishedAsync(message.Id, leaseToken, publishedAt, cancellationToken);

            _logger.LogInformation(
                "Successfully dispatched outbox message {MessageId} (Event: {EventId})",
                message.Id,
                message.EventId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var nextPublishAttempt = message.PublishAttemptCount + 1;

            _logger.LogError(
                ex,
                "Error publishing outbox message {MessageId} (PublishAttempt {Attempt}, ClaimCount {ClaimCount}): {ErrorMessage}",
                message.Id,
                nextPublishAttempt,
                message.ClaimCount,
                ex.Message);

            var errorMessage = $"{ex.GetType().Name}: {ex.Message}";

            if (nextPublishAttempt >= _options.MaxRetryAttempts)
            {
                // Max retry attempts reached -> mark as permanently failed
                await _outboxStore.MarkFailedAsync(
                    message.Id,
                    leaseToken,
                    errorMessage,
                    cancellationToken);
            }
            else
            {
                // Calculate conventional full jitter exponential backoff
                var backoffDelay = RetryPolicyHelper.CalculateFullJitterBackoff(
                    nextPublishAttempt,
                    _options.BaseRetryDelay,
                    _options.MaxRetryDelay);

                var nextAttemptAt = _timeProvider.GetUtcNow().Add(backoffDelay);

                await _outboxStore.ScheduleRetryAsync(
                    message.Id,
                    leaseToken,
                    nextAttemptAt,
                    errorMessage,
                    cancellationToken);
            }
        }
    }
}
