using System.Text.Json;

namespace Relay.Application.DTOs;

public sealed record IngestEventRequest(
    string? EventType,
    JsonElement Payload);
