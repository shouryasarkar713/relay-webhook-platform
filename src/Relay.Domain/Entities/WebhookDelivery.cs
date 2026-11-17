using Relay.Domain.Enums;

namespace Relay.Domain.Entities;

public sealed class WebhookDelivery
{
    private readonly List<WebhookDeliveryAttempt> _attempts = new();

    private WebhookDelivery() { }

    public WebhookDelivery(
        Guid id,
        Guid eventId,
        Guid endpointId,
        Guid tenantId,
        DateTimeOffset createdAtUtc,
        WebhookDeliveryStatus status = WebhookDeliveryStatus.Pending)
    {
        Id = id;
        EventId = eventId;
        EndpointId = endpointId;
        TenantId = tenantId;
        CreatedAtUtc = createdAtUtc;
        Status = status;
        ClaimCount = 0;
        AttemptCount = 0;
        RetryCycleAttemptCount = 0;
        ReplayCount = 0;
        NextAttemptAtUtc = createdAtUtc;
        RetryCycleStartedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }
    public Guid EventId { get; private set; }
    public Guid EndpointId { get; private set; }
    public Guid TenantId { get; private set; }
    public WebhookDeliveryStatus Status { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? LastAttemptAtUtc { get; private set; }
    public DateTimeOffset NextAttemptAtUtc { get; private set; }
    public DateTimeOffset? DeliveredAtUtc { get; private set; }
    public DateTimeOffset? DeadLetteredAtUtc { get; private set; }
    public int ClaimCount { get; private set; }
    public int AttemptCount { get; private set; }
    public int RetryCycleAttemptCount { get; private set; }
    public int ReplayCount { get; private set; }
    public DateTimeOffset RetryCycleStartedAtUtc { get; private set; }
    public int? LastHttpStatusCode { get; private set; }
    public string? LastErrorCategory { get; private set; }
    public string? LastError { get; private set; }

    public Guid? LeaseToken { get; private set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; private set; }

    public Event? Event { get; private set; }
    public WebhookEndpoint? Endpoint { get; private set; }
    public IReadOnlyCollection<WebhookDeliveryAttempt> Attempts => _attempts.AsReadOnly();

    public void Claim(Guid leaseToken, DateTimeOffset leaseExpiry, DateTimeOffset now)
    {
        if (Status != WebhookDeliveryStatus.Pending && Status != WebhookDeliveryStatus.Processing)
        {
            throw new InvalidOperationException($"Cannot claim delivery in status {Status}");
        }

        LeaseToken = leaseToken;
        LeaseExpiresAtUtc = leaseExpiry;
        Status = WebhookDeliveryStatus.Processing;
        ClaimCount++;
    }

    public void BeginAttempt(DateTimeOffset now)
    {
        AttemptCount++;
        RetryCycleAttemptCount++;
        LastAttemptAtUtc = now;
    }

    public void MarkDelivered(DateTimeOffset deliveredAtUtc, int httpStatusCode, Guid? expectedLeaseToken = null)
    {
        VerifyLeaseToken(expectedLeaseToken);

        Status = WebhookDeliveryStatus.Delivered;
        DeliveredAtUtc = deliveredAtUtc;
        LastAttemptAtUtc = deliveredAtUtc;
        LastHttpStatusCode = httpStatusCode;
        LastErrorCategory = null;
        LastError = null;
        LeaseToken = null;
        LeaseExpiresAtUtc = null;
    }

    public void ScheduleRetry(
        DateTimeOffset nextAttemptAtUtc,
        DateTimeOffset failedAtUtc,
        int? httpStatusCode,
        string errorCategory,
        string error,
        Guid? expectedLeaseToken = null)
    {
        VerifyLeaseToken(expectedLeaseToken);

        Status = WebhookDeliveryStatus.Pending;
        NextAttemptAtUtc = nextAttemptAtUtc;
        LastAttemptAtUtc = failedAtUtc;
        LastHttpStatusCode = httpStatusCode;
        LastErrorCategory = errorCategory;
        LastError = error;
        LeaseToken = null;
        LeaseExpiresAtUtc = null;
    }

    public void MarkPermanentFailure(
        DateTimeOffset failedAtUtc,
        int? httpStatusCode,
        string errorCategory,
        string error,
        Guid? expectedLeaseToken = null)
    {
        VerifyLeaseToken(expectedLeaseToken);

        Status = WebhookDeliveryStatus.Failed;
        LastAttemptAtUtc = failedAtUtc;
        LastHttpStatusCode = httpStatusCode;
        LastErrorCategory = errorCategory;
        LastError = error;
        LeaseToken = null;
        LeaseExpiresAtUtc = null;
    }

    public void DeadLetter(
        DateTimeOffset deadLetteredAtUtc,
        int? httpStatusCode,
        string errorCategory,
        string error,
        Guid? expectedLeaseToken = null)
    {
        VerifyLeaseToken(expectedLeaseToken);

        Status = WebhookDeliveryStatus.DeadLettered;
        DeadLetteredAtUtc = deadLetteredAtUtc;
        LastAttemptAtUtc = deadLetteredAtUtc;
        LastHttpStatusCode = httpStatusCode;
        LastErrorCategory = errorCategory;
        LastError = error;
        LeaseToken = null;
        LeaseExpiresAtUtc = null;
    }

    public void DeferForCircuitBreaker(
        DateTimeOffset nextAttemptAtUtc,
        string reason,
        Guid? expectedLeaseToken = null)
    {
        VerifyLeaseToken(expectedLeaseToken);

        Status = WebhookDeliveryStatus.Pending;
        NextAttemptAtUtc = nextAttemptAtUtc;
        LastErrorCategory = "CircuitBreakerDeferred";
        LastError = reason;
        LeaseToken = null;
        LeaseExpiresAtUtc = null;
    }

    public void MarkSkipped(DateTimeOffset skippedAtUtc, string reason, Guid? expectedLeaseToken = null)
    {
        VerifyLeaseToken(expectedLeaseToken);

        Status = WebhookDeliveryStatus.Skipped;
        LastAttemptAtUtc = skippedAtUtc;
        LastErrorCategory = "Skipped";
        LastError = reason;
        LeaseToken = null;
        LeaseExpiresAtUtc = null;
    }

    public void Replay(DateTimeOffset replayedAtUtc)
    {
        if (Status != WebhookDeliveryStatus.DeadLettered && Status != WebhookDeliveryStatus.Failed)
        {
            throw new InvalidOperationException($"Cannot replay delivery in status '{Status}'. Only DeadLettered or Failed deliveries can be replayed.");
        }

        ReplayCount++;
        RetryCycleAttemptCount = 0;
        RetryCycleStartedAtUtc = replayedAtUtc;
        Status = WebhookDeliveryStatus.Pending;
        NextAttemptAtUtc = replayedAtUtc;
        DeadLetteredAtUtc = null;
        LeaseToken = null;
        LeaseExpiresAtUtc = null;
    }

    public void AddAttempt(WebhookDeliveryAttempt attempt)
    {
        _attempts.Add(attempt);
    }

    private void VerifyLeaseToken(Guid? expectedLeaseToken)
    {
        if (expectedLeaseToken.HasValue && LeaseToken != expectedLeaseToken.Value)
        {
            throw new InvalidOperationException("Operation rejected: lease token mismatch or expired lease.");
        }
    }
}
