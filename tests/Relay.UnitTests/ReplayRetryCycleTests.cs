using FluentAssertions;
using Microsoft.Extensions.Options;
using Relay.Application.Options;
using Relay.Application.Services;
using Relay.Domain.Entities;
using Relay.Domain.Enums;
using Xunit;

namespace Relay.UnitTests;

public sealed class ReplayRetryCycleTests
{
    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;
        public TestTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
    }

    [Fact]
    public void Replay_ResetsRetryCycleAttemptCount_WhileMaintainingMonotonicAttemptCount()
    {
        var now = DateTimeOffset.UtcNow;
        var delivery = new WebhookDelivery(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            now);

        // Perform 5 initial attempts
        for (int i = 0; i < 5; i++)
        {
            delivery.BeginAttempt(now.AddMinutes(i));
        }

        delivery.AttemptCount.Should().Be(5);
        delivery.RetryCycleAttemptCount.Should().Be(5);

        // Transition to DeadLettered
        delivery.DeadLetter(
            now.AddMinutes(5),
            null,
            "Timeout",
            "Max attempts exceeded");

        delivery.Status.Should().Be(WebhookDeliveryStatus.DeadLettered);
        delivery.DeadLetteredAtUtc.Should().NotBeNull();

        // Perform Replay
        var replayedAt = now.AddHours(2);
        delivery.Replay(replayedAt);

        delivery.Status.Should().Be(WebhookDeliveryStatus.Pending);
        delivery.DeadLetteredAtUtc.Should().BeNull();
        delivery.NextAttemptAtUtc.Should().Be(replayedAt);
        delivery.ReplayCount.Should().Be(1);
        delivery.AttemptCount.Should().Be(5, "Lifetime attempts must remain monotonic for audit history");
        delivery.RetryCycleAttemptCount.Should().Be(0, "Retry cycle attempts must reset to allow fresh budget");
        delivery.RetryCycleStartedAtUtc.Should().Be(replayedAt);

        // First attempt in new cycle
        delivery.BeginAttempt(replayedAt.AddSeconds(1));
        delivery.AttemptCount.Should().Be(6);
        delivery.RetryCycleAttemptCount.Should().Be(1);
    }

    [Fact]
    public void RetryPolicy_WithReplayRetryCycle_GivesFresh24HourWindowForStaleEvents()
    {
        var initialCreation = new DateTimeOffset(2026, 11, 10, 10, 0, 0, TimeSpan.Zero);
        var threeDaysLater = initialCreation.AddDays(3);
        var timeProvider = new TestTimeProvider(threeDaysLater);

        var options = new DeliveryRetryOptions
        {
            MaxAttempts = 5,
            BaseDelaySeconds = 2,
            MaxDelaySeconds = 60,
            MaxRetryWindowHours = 24
        };

        var policy = new WebhookRetryPolicy(Options.Create(options), timeProvider, () => 0.5);

        // If evaluated against initialCreation (3 days old), retry window is expired!
        var expiredDecision = policy.EvaluateRetry(1, initialCreation, null);
        expiredDecision.ShouldRetry.Should().BeFalse();
        expiredDecision.TerminalReason.Should().Contain("Retry window of 24 hours expired");

        // When replayed today, retryCycleStart is current time
        var replayTime = timeProvider.GetUtcNow();
        var replayedDecision = policy.EvaluateRetry(1, replayTime, null);

        replayedDecision.ShouldRetry.Should().BeTrue();
        replayedDecision.NextAttemptAtUtc.Should().NotBeNull();
        replayedDecision.NextAttemptAtUtc.Should().BeAfter(replayTime);
    }
}
