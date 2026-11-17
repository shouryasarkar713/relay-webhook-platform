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
        NextAttemptAtUtc = createdAtUtc;
        Status = status;
        ClaimCount = 0;
        PublishAttemptCount = 0;
    }

    public Guid Id { get; private set; }
    public Guid EventId { get; private set; }
    public Guid TenantId { get; private set; }
    public string MessageType { get; private set; } = string.Empty;
    public string PayloadJson { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? PublishedAtUtc { get; private set; }
    public int ClaimCount { get; private set; }
    public int PublishAttemptCount { get; private set; }
    public string? LastError { get; private set; }
    public OutboxStatus Status { get; private set; }

    public Guid? LeaseToken { get; private set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; private set; }
    public DateTimeOffset NextAttemptAtUtc { get; private set; }
    public DateTimeOffset? LastAttemptAtUtc { get; private set; }

    public Event? Event { get; private set; }

    public void Claim(Guid leaseToken, DateTimeOffset leaseExpiry, DateTimeOffset now)
    {
        if (Status != OutboxStatus.Pending && Status != OutboxStatus.Processing)
        {
            throw new InvalidOperationException($"Cannot claim outbox message in {Status} status.");
        }

        LeaseToken = leaseToken;
        LeaseExpiresAtUtc = leaseExpiry;
        LastAttemptAtUtc = now;
        Status = OutboxStatus.Processing;
        ClaimCount++;
    }

    public void RecordPublishAttempt()
    {
        PublishAttemptCount++;
    }

    public void MarkPublished(DateTimeOffset publishedAtUtc, Guid? expectedLeaseToken = null)
    {
        if (expectedLeaseToken.HasValue && LeaseToken != expectedLeaseToken.Value)
        {
            throw new InvalidOperationException("Cannot mark published: lease token mismatch or expired lease.");
        }

        Status = OutboxStatus.Published;
        PublishedAtUtc = publishedAtUtc;
        LeaseToken = null;
        LeaseExpiresAtUtc = null;
        LastError = null;
    }

    public void ScheduleRetry(DateTimeOffset nextAttemptUtc, string error, DateTimeOffset now, Guid? expectedLeaseToken = null)
    {
        if (expectedLeaseToken.HasValue && LeaseToken != expectedLeaseToken.Value)
        {
            throw new InvalidOperationException("Cannot schedule retry: lease token mismatch or expired lease.");
        }

        Status = OutboxStatus.Pending;
        NextAttemptAtUtc = nextAttemptUtc;
        LastError = error;
        LeaseToken = null;
        LeaseExpiresAtUtc = null;
        LastAttemptAtUtc = now;
    }

    public void MarkFailed(string error, DateTimeOffset now, Guid? expectedLeaseToken = null)
    {
        if (expectedLeaseToken.HasValue && LeaseToken != expectedLeaseToken.Value)
        {
            throw new InvalidOperationException("Cannot mark failed: lease token mismatch or expired lease.");
        }

        Status = OutboxStatus.Failed;
        LastError = error;
        LeaseToken = null;
        LeaseExpiresAtUtc = null;
        LastAttemptAtUtc = now;
    }
}
