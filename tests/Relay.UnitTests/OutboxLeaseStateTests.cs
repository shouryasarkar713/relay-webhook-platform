using FluentAssertions;
using Relay.Domain.Entities;
using Relay.Domain.Enums;
using Xunit;

namespace Relay.UnitTests;

public sealed class OutboxLeaseStateTests
{
    [Fact]
    public void Claim_TransitionsToProcessingAndSetsLease()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var msg = new OutboxMessage(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "user.created", "{}", now);
        var leaseToken = Guid.NewGuid();
        var leaseExpiry = now.AddSeconds(30);

        // Act
        msg.Claim(leaseToken, leaseExpiry, now);

        // Assert
        msg.Status.Should().Be(OutboxStatus.Processing);
        msg.LeaseToken.Should().Be(leaseToken);
        msg.LeaseExpiresAtUtc.Should().Be(leaseExpiry);
        msg.LastAttemptAtUtc.Should().Be(now);
        msg.ClaimCount.Should().Be(1);
        msg.PublishAttemptCount.Should().Be(0);
    }

    [Fact]
    public void MarkPublished_WithMatchingLease_SucceedsAndClearsLease()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var msg = new OutboxMessage(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "user.created", "{}", now);
        var leaseToken = Guid.NewGuid();
        msg.Claim(leaseToken, now.AddSeconds(30), now);

        // Act
        var publishedAt = now.AddSeconds(2);
        msg.MarkPublished(publishedAt, expectedLeaseToken: leaseToken);

        // Assert
        msg.Status.Should().Be(OutboxStatus.Published);
        msg.PublishedAtUtc.Should().Be(publishedAt);
        msg.LeaseToken.Should().BeNull();
        msg.LeaseExpiresAtUtc.Should().BeNull();
    }

    [Fact]
    public void MarkPublished_WithMismatchedLease_ThrowsInvalidOperationException()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var msg = new OutboxMessage(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "user.created", "{}", now);
        var leaseToken = Guid.NewGuid();
        msg.Claim(leaseToken, now.AddSeconds(30), now);

        // Act
        var act = () => msg.MarkPublished(now.AddSeconds(2), expectedLeaseToken: Guid.NewGuid());

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*lease token mismatch*");
    }

    [Fact]
    public void ScheduleRetry_WithMatchingLease_TransitionsToPendingWithNextAttempt()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var msg = new OutboxMessage(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "user.created", "{}", now);
        var leaseToken = Guid.NewGuid();
        msg.Claim(leaseToken, now.AddSeconds(30), now);

        // Act
        var nextAttempt = now.AddSeconds(15);
        msg.ScheduleRetry(nextAttempt, "Transient network failure", now, expectedLeaseToken: leaseToken);

        // Assert
        msg.Status.Should().Be(OutboxStatus.Pending);
        msg.NextAttemptAtUtc.Should().Be(nextAttempt);
        msg.LastError.Should().Be("Transient network failure");
        msg.LeaseToken.Should().BeNull();
        msg.LeaseExpiresAtUtc.Should().BeNull();
    }

    [Fact]
    public void MarkFailed_WithMatchingLease_TransitionsToFailed()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var msg = new OutboxMessage(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "user.created", "{}", now);
        var leaseToken = Guid.NewGuid();
        msg.Claim(leaseToken, now.AddSeconds(30), now);

        // Act
        msg.MarkFailed("Exceeded max retry attempts", now, expectedLeaseToken: leaseToken);

        // Assert
        msg.Status.Should().Be(OutboxStatus.Failed);
        msg.LastError.Should().Be("Exceeded max retry attempts");
        msg.LeaseToken.Should().BeNull();
        msg.LeaseExpiresAtUtc.Should().BeNull();
    }
}
