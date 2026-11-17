using System.Text.Json;
using FluentAssertions;
using Relay.Application.Messaging;
using Xunit;

namespace Relay.UnitTests;

public sealed class OutboxEnvelopeTests
{
    [Fact]
    public void Create_PreservesNestedJson_WithoutDoubleSerialization()
    {
        // Arrange
        var eventId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var eventType = "invoice.paid";
        var occurredAt = DateTimeOffset.UtcNow;
        var rawJson = """{"invoiceId":"INV-999","amount":120.50,"items":[{"id":1,"desc":"Item A"}]}""";

        // Act
        var envelope = OutboxEnvelope.Create(eventId, tenantId, eventType, occurredAt, rawJson);
        var serialized = JsonSerializer.Serialize(envelope);

        // Assert
        envelope.SchemaVersion.Should().Be(1);
        envelope.EventId.Should().Be(eventId);
        envelope.TenantId.Should().Be(tenantId);
        envelope.EventType.Should().Be(eventType);
        envelope.OccurredAtUtc.Should().Be(occurredAt);

        // Verify payload is stored as native object, NOT escaped JSON string
        serialized.Should().Contain("\"invoiceId\":\"INV-999\"");
        serialized.Should().NotContain("\\\"invoiceId\\\"");

        using var roundtripDoc = JsonDocument.Parse(serialized);
        var payloadElement = roundtripDoc.RootElement.GetProperty("payload");
        payloadElement.ValueKind.Should().Be(JsonValueKind.Object);
        payloadElement.GetProperty("invoiceId").GetString().Should().Be("INV-999");
        payloadElement.GetProperty("amount").GetDecimal().Should().Be(120.50m);
    }
}
