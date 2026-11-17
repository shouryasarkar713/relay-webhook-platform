using Relay.Domain.Enums;

namespace Relay.Domain.Entities;

public sealed class OutboxMessage
{
    private OutboxMessage() { }

    public OutboxMessage(
        Guid id,
        Guid eventId,
        Guid tenantId,
        string messageType,
        string payloadJson,
        DateTimeOffset createdAtUtc,
        OutboxStatus status = OutboxStatus.Pending)
    {
        if (string.IsNullOrWhiteSpace(messageType))
        {
            throw new ArgumentException("Message type cannot be empty.", nameof(messageType));
        }

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            throw new ArgumentException("Payload JSON cannot be empty.", nameof(payloadJson));
        }

        Id = id;
        EventId = eventId;
        TenantId = tenantId;
        MessageType = messageType.Trim();
        PayloadJson = payloadJson;
        CreatedAtUtc = createdAtUtc;
        Status = status;
        AttemptCount = 0;
    }

    public Guid Id { get; private set; }
    public Guid EventId { get; private set; }
    public Guid TenantId { get; private set; }
    public string MessageType { get; private set; } = string.Empty;
    public string PayloadJson { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? PublishedAtUtc { get; private set; }
    public int AttemptCount { get; private set; }
    public string? LastError { get; private set; }
    public OutboxStatus Status { get; private set; }

    public Event? Event { get; private set; }

    public void MarkPublished(DateTimeOffset publishedAtUtc)
    {
        Status = OutboxStatus.Published;
        PublishedAtUtc = publishedAtUtc;
        LastError = null;
    }

    public void RecordFailure(string error)
    {
        AttemptCount++;
        LastError = error;
        Status = OutboxStatus.Failed;
    }
}
