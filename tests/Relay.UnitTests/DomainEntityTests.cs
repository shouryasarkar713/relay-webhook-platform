using FluentAssertions;
using Relay.Domain.Entities;
using Relay.Domain.Enums;
using Xunit;

namespace Relay.UnitTests;

public sealed class DomainEntityTests
{
    [Fact]
    public void Tenant_Constructor_InitializesCorrectlyAndEnforcesInvariants()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var tenantId = Guid.NewGuid();

        // Act
        var tenant = new Tenant(tenantId, "  Acme Corp  ", "ACME-CORP", now);

        // Assert
        tenant.Id.Should().Be(tenantId);
        tenant.Name.Should().Be("Acme Corp");
        tenant.Slug.Should().Be("acme-corp");
        tenant.IsActive.Should().BeTrue();
        tenant.CreatedAtUtc.Should().Be(now);

        tenant.Deactivate();
        tenant.IsActive.Should().BeFalse();

        tenant.Activate();
        tenant.IsActive.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Tenant_Constructor_WithInvalidName_ThrowsArgumentException(string invalidName)
    {
        var act = () => new Tenant(Guid.NewGuid(), invalidName, "slug", DateTimeOffset.UtcNow);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ApiKey_IsValid_EnforcesRevocationAndExpiration()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var key = new ApiKey(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Production Key",
            "pk_prod",
            "hash123",
            now,
            expiresAtUtc: now.AddDays(30));

        // Act & Assert
        key.IsValid(now).Should().BeTrue();
        key.IsRevoked.Should().BeFalse();
        key.IsExpired(now).Should().BeFalse();

        // Expire test
        key.IsExpired(now.AddDays(31)).Should().BeTrue();
        key.IsValid(now.AddDays(31)).Should().BeFalse();

        // Revoke test
        key.Revoke(now.AddDays(1));
        key.IsRevoked.Should().BeTrue();
        key.IsValid(now).Should().BeFalse();
    }

    [Fact]
    public void OutboxMessage_MarkPublished_UpdatesStatusAndClearsError()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var msg = new OutboxMessage(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "order.created",
            "{}",
            now);

        msg.Status.Should().Be(OutboxStatus.Pending);
        msg.AttemptCount.Should().Be(0);

        // Act
        msg.RecordFailure("Network timeout connecting to broker");
        msg.Status.Should().Be(OutboxStatus.Failed);
        msg.AttemptCount.Should().Be(1);
        msg.LastError.Should().Contain("timeout");

        var publishedAt = now.AddMinutes(5);
        msg.MarkPublished(publishedAt);
        msg.Status.Should().Be(OutboxStatus.Published);
        msg.PublishedAtUtc.Should().Be(publishedAt);
        msg.LastError.Should().BeNull();
    }
}
