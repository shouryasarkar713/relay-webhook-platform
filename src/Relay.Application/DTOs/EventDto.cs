namespace Relay.Application.DTOs;

public sealed record EventDto(
    Guid Id,
    Guid TenantId,
    string EventType,
    string PayloadJson,
    string PayloadHash,
    string IdempotencyKey,
    DateTimeOffset CreatedAtUtc,
    string Status);
