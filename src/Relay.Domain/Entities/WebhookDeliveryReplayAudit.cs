using Relay.Domain.Enums;

namespace Relay.Domain.Entities;

public sealed class WebhookDeliveryReplayAudit
{
    private WebhookDeliveryReplayAudit() { }

    public WebhookDeliveryReplayAudit(
        Guid id,
        Guid tenantId,
        Guid deliveryId,
        string? replayKey,
        string requestedBy,
        WebhookDeliveryStatus previousStatus,
        DateTimeOffset replayedAtUtc)
    {
        Id = id;
        TenantId = tenantId;
        DeliveryId = deliveryId;
        ReplayKey = replayKey?.Trim();
        RequestedBy = string.IsNullOrWhiteSpace(requestedBy) ? "ApiKey" : requestedBy.Trim();
        PreviousStatus = previousStatus;
        ReplayedAtUtc = replayedAtUtc;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid DeliveryId { get; private set; }
    public string? ReplayKey { get; private set; }
    public string RequestedBy { get; private set; } = string.Empty;
    public WebhookDeliveryStatus PreviousStatus { get; private set; }
    public DateTimeOffset ReplayedAtUtc { get; private set; }

    public WebhookDelivery? Delivery { get; private set; }
}
