using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Relay.Application.Abstractions;
using Relay.Application.DTOs;
using Relay.Application.Exceptions;
using Relay.Application.Services;
using Relay.Domain.Entities;
using Xunit;

namespace Relay.UnitTests;

public sealed class EventIngestionServiceUnitTests
{
    private sealed class FakeTenantContext : ITenantContext
    {
        public Guid TenantId { get; set; } = Guid.NewGuid();
        public Guid ApiKeyId { get; set; } = Guid.NewGuid();
        public bool IsAuthenticated { get; set; } = true;
    }

    [Fact]
    public async Task IngestAsync_WhenTenantNotAuthenticated_ThrowsUnauthorizedTenantException()
    {
        // Arrange
        var unauthenticatedContext = new FakeTenantContext { IsAuthenticated = false };
        var service = new EventIngestionService(
            null!, // Should throw before DB access
            unauthenticatedContext,
            TimeProvider.System,
            NullLogger<EventIngestionService>.Instance);

        var request = new IngestEventRequest("order.created", JsonDocument.Parse("{}").RootElement);

        // Act & Assert
        var act = () => service.IngestAsync(request, "idem_123");
        await act.Should().ThrowAsync<UnauthorizedTenantException>();
    }

    [Fact]
    public async Task IngestAsync_WhenPayloadExceeds32KB_ThrowsPayloadTooLargeException()
    {
        // Arrange
        var context = new FakeTenantContext { IsAuthenticated = true };
        var service = new EventIngestionService(
            null!,
            context,
            TimeProvider.System,
            NullLogger<EventIngestionService>.Instance);

        // 35 KB payload
        var largeString = new string('A', 35 * 1024);
        var largePayload = JsonDocument.Parse($$"""{"data": "{{largeString}}"}""").RootElement;
        var request = new IngestEventRequest("large.event", largePayload);

        // Act & Assert
        var act = () => service.IngestAsync(request, "idem_large");
        await act.Should().ThrowAsync<PayloadTooLargeException>()
            .Where(e => e.MaxAllowedBytes == 32 * 1024);
    }
}
