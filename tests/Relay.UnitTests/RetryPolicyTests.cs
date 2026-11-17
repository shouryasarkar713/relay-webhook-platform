using FluentAssertions;
using Microsoft.Extensions.Options;
using Relay.Application.Abstractions;
using Relay.Application.Options;
using Relay.Application.Services;
using Xunit;

namespace Relay.UnitTests;

public sealed class RetryPolicyTests
{
    private readonly DeliveryRetryOptions _options = new()
    {
        MaxAttempts = 5,
        BaseDelaySeconds = 2.0,
        MaxDelaySeconds = 60.0,
        MaxRetryWindowHours = 24,
        MaxServerRequestedDelaySeconds = 86400.0
    };

    [Fact]
    public void EvaluateRetry_FirstAttempt_AppliesFullJitterFormula()
    {
        // Arrange: attempt 1 -> multiplier = 2^0 = 1, cap = 2.0s
        // Jitter = 0.5 -> delay = 1.0s
        var fixedTime = new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(fixedTime);
        var policy = new WebhookRetryPolicy(Options.Create(_options), timeProvider, () => 0.5);

        // Act
        var decision = policy.EvaluateRetry(1, fixedTime, serverRetryAfter: null);

        // Assert
        decision.ShouldRetry.Should().BeTrue();
        decision.Delay.TotalSeconds.Should().Be(1.0);
        decision.NextAttemptAtUtc.Should().Be(fixedTime.AddSeconds(1.0));
        decision.TerminalReason.Should().BeNull();
    }

    [Fact]
    public void EvaluateRetry_LaterAttempt_CapsAtMaxDelaySeconds()
    {
        // Arrange: attempt 10 -> 2.0 * 2^9 = 1024, capped at MaxDelaySeconds = 60.0
        // Jitter = 1.0 -> delay = 60.0s
        var fixedTime = new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(fixedTime);
        var customOptions = new DeliveryRetryOptions
        {
            MaxAttempts = 15,
            BaseDelaySeconds = 2.0,
            MaxDelaySeconds = 60.0,
            MaxRetryWindowHours = 24
        };
        var policy = new WebhookRetryPolicy(Options.Create(customOptions), timeProvider, () => 1.0);

        // Act
        var decision = policy.EvaluateRetry(10, fixedTime, serverRetryAfter: null);

        // Assert
        decision.ShouldRetry.Should().BeTrue();
        decision.Delay.TotalSeconds.Should().Be(60.0);
    }

    [Fact]
    public void EvaluateRetry_AttemptExceedsMaxAttempts_ExhaustsRetryBudget()
    {
        // Arrange: max attempts = 5, attemptCount = 5
        var fixedTime = new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(fixedTime);
        var policy = new WebhookRetryPolicy(Options.Create(_options), timeProvider);

        // Act
        var decision = policy.EvaluateRetry(5, fixedTime, serverRetryAfter: null);

        // Assert
        decision.ShouldRetry.Should().BeFalse();
        decision.NextAttemptAtUtc.Should().BeNull();
        decision.TerminalReason.Should().Contain("Retry budget exhausted after 5 attempts");
    }

    [Fact]
    public void EvaluateRetry_ServerRetryAfterExceedsMaxHorizon_ReturnsTerminalFailure()
    {
        var fixedTime = new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(fixedTime);
        var policy = new WebhookRetryPolicy(Options.Create(_options), timeProvider);

        var parsedRetryAfter = new ParsedRetryAfter(
            TimeSpan.FromDays(2),
            fixedTime.AddDays(2),
            ExceedsMaxHorizon: true);

        // Act
        var decision = policy.EvaluateRetry(1, fixedTime, parsedRetryAfter);

        // Assert
        decision.ShouldRetry.Should().BeFalse();
        decision.TerminalReason.Should().Contain("exceeds maximum allowed horizon");
    }

    [Fact]
    public void EvaluateRetry_ServerRetryAfterLargerThanJitter_RespectsServerRequestedDelay()
    {
        var fixedTime = new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(fixedTime);
        // Calculated jitter delay = 0.5 * 2 = 1.0s
        var policy = new WebhookRetryPolicy(Options.Create(_options), timeProvider, () => 0.5);

        // Server requested 30 seconds
        var parsedRetryAfter = new ParsedRetryAfter(
            TimeSpan.FromSeconds(30),
            fixedTime.AddSeconds(30),
            ExceedsMaxHorizon: false);

        // Act
        var decision = policy.EvaluateRetry(1, fixedTime, parsedRetryAfter);

        // Assert
        decision.ShouldRetry.Should().BeTrue();
        decision.Delay.TotalSeconds.Should().Be(30.0, "Must not retry earlier than server requested");
        decision.NextAttemptAtUtc.Should().Be(fixedTime.AddSeconds(30));
    }

    [Fact]
    public void EvaluateRetry_ExceedsMaxRetryWindow_ReturnsTerminalFailure()
    {
        var fixedTime = new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(fixedTime);
        var policy = new WebhookRetryPolicy(Options.Create(_options), timeProvider, () => 1.0);

        // Delivery was created 24 hours and 1 minute ago.
        var createdAtUtc = fixedTime.AddHours(-24).AddMinutes(-1);

        // Act
        var decision = policy.EvaluateRetry(1, createdAtUtc, serverRetryAfter: null);

        // Assert
        decision.ShouldRetry.Should().BeFalse();
        decision.TerminalReason.Should().Contain("Retry window of 24 hours expired");
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;
        public TestTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
