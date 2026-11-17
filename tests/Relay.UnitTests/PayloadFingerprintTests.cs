using System.Text.Json;
using FluentAssertions;
using Relay.Application.Common;
using Xunit;

namespace Relay.UnitTests;

public sealed class PayloadFingerprintTests
{
    [Fact]
    public void Compute_ProducesDeterministicSha256HexRegardlessOfWhitespace()
    {
        // Arrange
        var jsonIndented = JsonDocument.Parse("""
        {
            "orderId": "ORD-1234",
            "amount": 99.95,
            "currency": "USD"
        }
        """).RootElement;

        var jsonMinified = JsonDocument.Parse("""{"orderId":"ORD-1234","amount":99.95,"currency":"USD"}""").RootElement;

        // Act
        var result1 = PayloadFingerprint.Compute(jsonIndented);
        var result2 = PayloadFingerprint.Compute(jsonMinified);

        // Assert
        result1.HashHex.Should().Be(result2.HashHex);
        result1.MinifiedJson.Should().Be(result2.MinifiedJson);
        result1.ByteCount.Should().Be(result2.ByteCount);
        result1.HashHex.Length.Should().Be(64);
    }

    [Fact]
    public void Compute_WithDifferentPayload_ProducesDifferentHash()
    {
        // Arrange
        var json1 = JsonDocument.Parse("""{"amount": 100}""").RootElement;
        var json2 = JsonDocument.Parse("""{"amount": 101}""").RootElement;

        // Act
        var result1 = PayloadFingerprint.Compute(json1);
        var result2 = PayloadFingerprint.Compute(json2);

        // Assert
        result1.HashHex.Should().NotBe(result2.HashHex);
    }

    [Fact]
    public void Compute_AccuratelyCalculatesUtf8ByteLength()
    {
        // Arrange (contains multi-byte unicode characters: ₹ symbol is 3 bytes in UTF-8)
        var json = JsonDocument.Parse("""{"currency":"₹"}""").RootElement;

        // Act
        var (minified, _, byteCount) = PayloadFingerprint.Compute(json);

        // Assert
        // {"currency":"₹"} is 13 ASCII chars + 3 byte UTF-8 ₹ + 2 ASCII chars = 18 bytes
        minified.Should().Be("""{"currency":"₹"}""");
        byteCount.Should().Be(18);
    }
}
