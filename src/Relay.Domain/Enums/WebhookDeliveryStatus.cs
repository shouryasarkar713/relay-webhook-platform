namespace Relay.Domain.Enums;

public enum WebhookDeliveryStatus
{
    Pending = 1,
    Processing = 2,
    Delivered = 3,
    Failed = 4,
    Skipped = 5,
    DeadLettered = 6
}
