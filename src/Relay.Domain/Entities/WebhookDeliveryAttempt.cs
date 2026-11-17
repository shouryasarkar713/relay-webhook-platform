using Relay.Domain.Enums;

namespace Relay.Domain.Entities;

public sealed class WebhookDeliveryAttempt
{
    private WebhookDeliveryAttempt() { }

    public WebhookDeliveryAttempt(
        Guid id,
        Guid deliveryId,
        int attemptNumber,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        long durationMilliseconds,
        int? httpStatusCode,
        DeliveryAttemptOutcome outcome,
        string? errorCategory,
        string? responseExcerpt)
    {
        Id = id;
        DeliveryId = deliveryId;
        AttemptNumber = attemptNumber;
        StartedAtUtc = startedAtUtc;
        CompletedAtUtc = completedAtUtc;
        DurationMilliseconds = durationMilliseconds;
        HttpStatusCode = httpStatusCode;
        Outcome = outcome;
        ErrorCategory = errorCategory;
        ResponseExcerpt = responseExcerpt;
    }

    public Guid Id { get; private set; }
    public Guid DeliveryId { get; private set; }
    public int AttemptNumber { get; private set; }
    public DateTimeOffset StartedAtUtc { get; private set; }
    public DateTimeOffset CompletedAtUtc { get; private set; }
    public long DurationMilliseconds { get; private set; }
    public int? HttpStatusCode { get; private set; }
    public DeliveryAttemptOutcome Outcome { get; private set; }
    public string? ErrorCategory { get; private set; }
    public string? ResponseExcerpt { get; private set; }

    public WebhookDelivery? Delivery { get; private set; }
}
