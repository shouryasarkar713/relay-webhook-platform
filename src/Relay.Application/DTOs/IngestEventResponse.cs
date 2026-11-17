namespace Relay.Application.DTOs;

public sealed record IngestEventResponse(
    Guid EventId,
    string Status,
    DateTimeOffset CreatedAtUtc);
