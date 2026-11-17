using FluentAssertions;
using Relay.Application.Services;
using Xunit;

namespace Relay.UnitTests;

public sealed class RetryAfterParserTests
{
    private readonly DateTimeOffset _fixedNow = new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
    private readonly TimeSpan _maxHorizon = TimeSpan.FromHours(24);

    [Fact]
    public void ParseRaw_ValidDeltaSeconds_ReturnsCorrectDelay()
    {
        var parser = new RetryAfterParser(new TestTimeProvider(_fixedNow));
        var result = parser.ParseRaw("120", _maxHorizon);

        result.Should().NotBeNull();
        result!.RequestedDelay.Should().Be(TimeSpan.FromSeconds(120));
        result.TargetTimeUtc.Should().Be(_fixedNow.AddSeconds(120));
        result.ExceedsMaxHorizon.Should().BeFalse();
    }

    [Fact]
    public void ParseRaw_DeltaSecondsExceedingHorizon_FlagsExceedsMaxHorizon()
    {
        var parser = new RetryAfterParser(new TestTimeProvider(_fixedNow));
        // 48 hours in seconds = 172800
        var result = parser.ParseRaw("172800", _maxHorizon);

        result.Should().NotBeNull();
        result!.RequestedDelay.Should().Be(TimeSpan.FromHours(48));
        result.ExceedsMaxHorizon.Should().BeTrue();
    }

    [Fact]
    public void ParseRaw_ValidHttpDateRfc1123_ReturnsCalculatedDelay()
    {
        var parser = new RetryAfterParser(new TestTimeProvider(_fixedNow));
        // 10 minutes in the future
        var targetDate = _fixedNow.AddMinutes(10);
        var rfc1123 = targetDate.ToString("R");

        var result = parser.ParseRaw(rfc1123, _maxHorizon);

        result.Should().NotBeNull();
        result!.RequestedDelay.Should().Be(TimeSpan.FromMinutes(10));
        result.TargetTimeUtc.Should().Be(targetDate);
        result.ExceedsMaxHorizon.Should().BeFalse();
    }

    [Fact]
    public void ParseRaw_PastHttpDate_ReturnsZeroDelay()
    {
        var parser = new RetryAfterParser(new TestTimeProvider(_fixedNow));
        var pastDate = _fixedNow.AddMinutes(-5);
        var rfc1123 = pastDate.ToString("R");

        var result = parser.ParseRaw(rfc1123, _maxHorizon);

        result.Should().NotBeNull();
        result!.RequestedDelay.Should().Be(TimeSpan.Zero);
        result.ExceedsMaxHorizon.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("invalid_value")]
    [InlineData("-10")]
    public void ParseRaw_InvalidOrNegativeValues_ReturnsNull(string? rawValue)
    {
        var parser = new RetryAfterParser(new TestTimeProvider(_fixedNow));
        var result = parser.ParseRaw(rawValue, _maxHorizon);

        result.Should().BeNull();
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public TestTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
