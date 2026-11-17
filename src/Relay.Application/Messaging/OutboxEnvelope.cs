using System.Text.Json;
using System.Text.Json.Serialization;

namespace Relay.Application.Messaging;

public sealed record OutboxEnvelope
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("eventId")]
    public Guid EventId { get; init; }

    [JsonPropertyName("tenantId")]
    public Guid TenantId { get; init; }

    [JsonPropertyName("eventType")]
    public string EventType { get; init; } = string.Empty;

    [JsonPropertyName("occurredAtUtc")]
    public DateTimeOffset OccurredAtUtc { get; init; }

    [JsonPropertyName("payload")]
    public JsonElement Payload { get; init; }

    public static OutboxEnvelope Create(
        Guid eventId,
        Guid tenantId,
        string eventType,
        DateTimeOffset occurredAtUtc,
        string payloadJson)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        return new OutboxEnvelope
        {
            SchemaVersion = 1,
            EventId = eventId,
            TenantId = tenantId,
            EventType = eventType,
            OccurredAtUtc = occurredAtUtc,
            Payload = doc.RootElement.Clone()
        };
    }
}
