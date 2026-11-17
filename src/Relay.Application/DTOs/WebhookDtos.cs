namespace Relay.Application.DTOs;

public sealed record CreateWebhookEndpointRequest(
    string Name,
    string Url,
    IReadOnlyList<string> EventTypes);

public sealed record UpdateWebhookEndpointRequest(
    string Name,
    string Url,
    IReadOnlyList<string> EventTypes,
    bool? IsActive = null);

public sealed record WebhookEndpointResponse(
    Guid Id,
    string Name,
    string Url,
    IReadOnlyList<string> EventTypes,
    bool IsActive,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? DisabledAtUtc,
    DateTimeOffset? LastDeliveryAtUtc,
    string? SigningSecret = null,
    string? CircuitState = "Closed",
    int ConsecutiveFailureCount = 0);

public sealed record WebhookDeliveryAttemptDto(
    Guid Id,
    int AttemptNumber,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    long DurationMilliseconds,
    int? HttpStatusCode,
    string Outcome,
    string? ErrorCategory,
    string? ResponseExcerpt);

public sealed record WebhookDeliveryDto(
    Guid Id,
    Guid EventId,
    Guid EndpointId,
    string? EndpointName,
    string? EndpointUrl,
    string? EventType,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastAttemptAtUtc,
    DateTimeOffset NextAttemptAtUtc,
    DateTimeOffset? DeliveredAtUtc,
    DateTimeOffset? DeadLetteredAtUtc,
    int ClaimCount,
    int AttemptCount,
    int RetryCycleAttemptCount,
    int ReplayCount,
    int? LastHttpStatusCode,
    string? LastErrorCategory,
    string? LastError,
    IReadOnlyList<WebhookDeliveryAttemptDto>? Attempts = null);

public sealed record ReplayDeliveryResponse(
    Guid ReplayId,
    Guid DeliveryId,
    string Status,
    DateTimeOffset ReplayedAtUtc,
    int ReplayCount,
    string Message);
