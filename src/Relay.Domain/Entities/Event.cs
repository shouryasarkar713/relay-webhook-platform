using Relay.Domain.Enums;

namespace Relay.Domain.Entities;

public sealed class Event
{
    private Event() { }

    public Event(
        Guid id,
        Guid tenantId,
        string eventType,
        string payloadJson,
        string payloadHash,
        string idempotencyKey,
        DateTimeOffset createdAtUtc,
        EventStatus status = EventStatus.Accepted)
    {
        if (string.IsNullOrWhiteSpace(eventType))
        {
            throw new ArgumentException("Event type cannot be empty.", nameof(eventType));
        }

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            throw new ArgumentException("Payload JSON cannot be empty.", nameof(payloadJson));
        }

        if (string.IsNullOrWhiteSpace(payloadHash))
        {
            throw new ArgumentException("Payload hash cannot be empty.", nameof(payloadHash));
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new ArgumentException("Idempotency key cannot be empty.", nameof(idempotencyKey));
        }

        Id = id;
        TenantId = tenantId;
        EventType = eventType.Trim();
        PayloadJson = payloadJson;
        PayloadHash = payloadHash.Trim();
        IdempotencyKey = idempotencyKey.Trim();
        CreatedAtUtc = createdAtUtc;
        Status = status;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string EventType { get; private set; } = string.Empty;
    public string PayloadJson { get; private set; } = string.Empty;
    public string PayloadHash { get; private set; } = string.Empty;
    public string IdempotencyKey { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public EventStatus Status { get; private set; }

    public Tenant? Tenant { get; private set; }
    public OutboxMessage? OutboxMessage { get; private set; }

    public void UpdateStatus(EventStatus newStatus)
    {
        Status = newStatus;
    }
}
