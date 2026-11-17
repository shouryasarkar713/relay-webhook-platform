using Relay.Domain.Entities;
using Relay.Domain.Enums;

namespace Relay.Application.Abstractions;

public interface IWebhookDeliveryStore
{
    Task<IReadOnlyList<WebhookDelivery>> ClaimPendingDeliveriesAsync(
        int batchSize,
        Guid leaseToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    Task<bool> BeginDeliveryAttemptAsync(
        Guid deliveryId,
        Guid leaseToken,
        WebhookDeliveryAttempt initialAttempt,
        CancellationToken cancellationToken = default);

    Task<bool> CompleteDeliveryAttemptAsync(
        Guid deliveryId,
        Guid leaseToken,
        WebhookDeliveryAttempt completedAttempt,
        WebhookDeliveryStatus targetStatus,
        DateTimeOffset? nextAttemptAtUtc,
        DateTimeOffset? deadLetteredAtUtc,
        string? errorCategory,
        string? errorMessage,
        CancellationToken cancellationToken = default);

    Task<bool> DeferDeliveryForCircuitBreakerAsync(
        Guid deliveryId,
        Guid leaseToken,
        DateTimeOffset nextAttemptAtUtc,
        string reason,
        CancellationToken cancellationToken = default);

    Task<WebhookDeliveryReplayAudit> ReplayDeliveryAsync(
        Guid deliveryId,
        Guid tenantId,
        string? replayKey,
        string requestedBy,
        DateTimeOffset replayedAtUtc,
        CancellationToken cancellationToken = default);
}
