using FluentAssertions;
using Relay.Worker.Options;
using Relay.Worker.Utilities;
using Xunit;

namespace Relay.UnitTests;

public sealed class RetryPolicyHelperTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void CalculateFullJitterBackoff_StaysWithinBounds(int attempt)
    {
        // Arrange
        var baseDelay = TimeSpan.FromSeconds(2);
        var maxDelay = TimeSpan.FromMinutes(1);

        // Act
        for (var i = 0; i < 50; i++)
        {
            var delay = RetryPolicyHelper.CalculateFullJitterBackoff(attempt, baseDelay, maxDelay);

            // Assert
            delay.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
            delay.Should().BeLessThanOrEqualTo(maxDelay);
        }
    }

    [Fact]
    public void CalculateFullJitterBackoff_CapsAtMaxDelay()
    {
        // Arrange
        var baseDelay = TimeSpan.FromSeconds(2);
        var maxDelay = TimeSpan.FromSeconds(10);

        // Act
        for (var i = 0; i < 100; i++)
        {
            var delay = RetryPolicyHelper.CalculateFullJitterBackoff(attemptCount: 15, baseDelay, maxDelay);

            // Assert
            delay.Should().BeLessThanOrEqualTo(maxDelay);
        }
    }

    [Fact]
    public void CalculateFullJitterBackoff_DeterministicJitter_CalculatesExpectedValues()
    {
        var baseDelay = TimeSpan.FromSeconds(2);
        var maxDelay = TimeSpan.FromSeconds(60);

        // Attempt 1: 2 * 2^0 = 2s. Fraction 0.5 => 1.0s
        var delay1 = RetryPolicyHelper.CalculateFullJitterBackoff(1, baseDelay, maxDelay, 0.5);
        delay1.Should().Be(TimeSpan.FromSeconds(1.0));

        // Attempt 2: 2 * 2^1 = 4s. Fraction 0.75 => 3.0s
        var delay2 = RetryPolicyHelper.CalculateFullJitterBackoff(2, baseDelay, maxDelay, 0.75);
        delay2.Should().Be(TimeSpan.FromSeconds(3.0));

        // Attempt 3: 2 * 2^2 = 8s. Fraction 1.0 => 8.0s
        var delay3 = RetryPolicyHelper.CalculateFullJitterBackoff(3, baseDelay, maxDelay, 1.0);
        delay3.Should().Be(TimeSpan.FromSeconds(8.0));
    }

    [Fact]
    public void OutboxDispatcherOptions_Validate_ThrowsOnInvalidConfigurations()
    {
        // Assert invalid batch size
        var opt1 = new OutboxDispatcherOptions { BatchSize = 0 };
        var act1 = () => opt1.Validate();
        act1.Should().Throw<InvalidOperationException>();

        // Assert invalid polling interval
        var opt2 = new OutboxDispatcherOptions { PollingInterval = TimeSpan.Zero };
        var act2 = () => opt2.Validate();
        act2.Should().Throw<InvalidOperationException>();

        // Assert max retry delay less than base retry delay
        var opt3 = new OutboxDispatcherOptions
        {
            BaseRetryDelay = TimeSpan.FromSeconds(10),
            MaxRetryDelay = TimeSpan.FromSeconds(5)
        };
        var act3 = () => opt3.Validate();
        act3.Should().Throw<InvalidOperationException>();
    }
}
