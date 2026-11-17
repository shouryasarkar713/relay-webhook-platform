using System.Text.Json;
using FluentAssertions;
using Relay.Application.DTOs;
using Relay.Application.Exceptions;
using Relay.Application.Validators;
using Xunit;

namespace Relay.UnitTests;

public sealed class IngestEventValidatorTests
{
    private static readonly JsonElement ValidPayload = JsonDocument.Parse("""{"key": "value"}""").RootElement;

    [Fact]
    public void Validate_WithValidRequest_DoesNotThrow()
    {
        // Arrange
        var request = new IngestEventRequest("order.created", ValidPayload);
        const string idempotencyKey = "idem_12345";

        // Act & Assert
        var act = () => IngestEventValidator.Validate(request, idempotencyKey);
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WithMissingIdempotencyKey_ThrowsValidationException(string? invalidKey)
    {
        // Arrange
        var request = new IngestEventRequest("order.created", ValidPayload);

        // Act & Assert
        var act = () => IngestEventValidator.Validate(request, invalidKey);
        act.Should().Throw<ValidationException>()
            .Which.Errors.Should().ContainKey("Idempotency-Key");
    }

    [Fact]
    public void Validate_WithIdempotencyKeyExceeding256Chars_ThrowsValidationException()
    {
        // Arrange
        var tooLongKey = new string('x', 257);
        var request = new IngestEventRequest("order.created", ValidPayload);

        // Act & Assert
        var act = () => IngestEventValidator.Validate(request, tooLongKey);
        act.Should().Throw<ValidationException>()
            .Which.Errors.Should().ContainKey("Idempotency-Key");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WithMissingEventType_ThrowsValidationException(string? invalidType)
    {
        // Arrange
        var request = new IngestEventRequest(invalidType, ValidPayload);

        // Act & Assert
        var act = () => IngestEventValidator.Validate(request, "idem_valid");
        act.Should().Throw<ValidationException>()
            .Which.Errors.Should().ContainKey("EventType");
    }

    [Theory]
    [InlineData("order created with spaces")]
    [InlineData("order@created")]
    [InlineData("order#created")]
    [InlineData("order$created")]
    public void Validate_WithInvalidEventTypeCharacters_ThrowsValidationException(string invalidType)
    {
        // Arrange
        var request = new IngestEventRequest(invalidType, ValidPayload);

        // Act & Assert
        var act = () => IngestEventValidator.Validate(request, "idem_valid");
        act.Should().Throw<ValidationException>()
            .Which.Errors.Should().ContainKey("EventType");
    }

    [Theory]
    [InlineData("order.created")]
    [InlineData("ORDER_UPDATED")]
    [InlineData("payment-refunded-v2")]
    [InlineData("billing.subscription.canceled")]
    public void Validate_WithAllowedEventTypeFormats_Succeeds(string validEventType)
    {
        // Arrange
        var request = new IngestEventRequest(validEventType, ValidPayload);

        // Act & Assert
        var act = () => IngestEventValidator.Validate(request, "idem_valid");
        act.Should().NotThrow();
    }
}
