using FluentAssertions;
using Relay.Infrastructure.Services;
using Xunit;

namespace Relay.UnitTests;

public sealed class ApiKeyHasherTests
{
    private readonly ApiKeyHasher _hasher = new();

    [Fact]
    public void TryParseKey_WithValidLiveKey_ReturnsTrueAndExtractsParts()
    {
        // Arrange
        const string rawKey = "rl_live_pk_test123_4a5b6c7d8e9f0123456789abcdef";

        // Act
        var success = _hasher.TryParseKey(rawKey, out var parts);

        // Assert
        success.Should().BeTrue();
        parts.Environment.Should().Be("live");
        parts.Prefix.Should().Be("pk_test123");
        parts.Secret.Should().Be("4a5b6c7d8e9f0123456789abcdef");
    }

    [Fact]
    public void TryParseKey_WithValidTestKey_ReturnsTrue()
    {
        // Arrange
        const string rawKey = "rl_test_prefix99_secrettokenvalue";

        // Act
        var success = _hasher.TryParseKey(rawKey, out var parts);

        // Assert
        success.Should().BeTrue();
        parts.Environment.Should().Be("test");
        parts.Prefix.Should().Be("prefix99");
        parts.Secret.Should().Be("secrettokenvalue");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("invalid_key_format")]
    [InlineData("rl_prod_prefix_secret")]
    [InlineData("rl_live_onlythreeparts")]
    [InlineData("rl_live__emptyprefix")]
    [InlineData("notrl_live_prefix_secret")]
    public void TryParseKey_WithMalformedKeys_ReturnsFalse(string malformed)
    {
        // Act
        var success = _hasher.TryParseKey(malformed, out _);

        // Assert
        success.Should().BeFalse();
    }

    [Fact]
    public void HashSecret_ComputesDeterministicSha256Hex()
    {
        // Arrange
        const string secret = "super_secret_payload_12345";

        // Act
        var hash1 = _hasher.HashSecret(secret);
        var hash2 = _hasher.HashSecret(secret);

        // Assert
        hash1.Should().NotBeNullOrWhiteSpace();
        hash1.Length.Should().Be(64); // 256 bits = 64 hex chars
        hash1.Should().Be(hash2);
    }

    [Fact]
    public void VerifySecret_WithMatchingSecret_ReturnsTrue()
    {
        // Arrange
        const string secret = "correct_secret_string";
        var expectedHash = _hasher.HashSecret(secret);

        // Act
        var isMatch = _hasher.VerifySecret(secret, expectedHash);

        // Assert
        isMatch.Should().BeTrue();
    }

    [Fact]
    public void VerifySecret_WithIncorrectSecret_ReturnsFalse()
    {
        // Arrange
        var expectedHash = _hasher.HashSecret("correct_secret_string");

        // Act
        var isMatch = _hasher.VerifySecret("wrong_secret_string", expectedHash);

        // Assert
        isMatch.Should().BeFalse();
    }

    [Fact]
    public void GenerateApiKey_ProducesValidFormatAndMatchesHash()
    {
        // Act
        var hash = _hasher.GenerateApiKey("live", "pk_auto01", out var rawKey);

        // Assert
        rawKey.Should().StartWith("rl_live_pk_auto01_");
        _hasher.TryParseKey(rawKey, out var parts).Should().BeTrue();
        parts.Prefix.Should().Be("pk_auto01");
        _hasher.VerifySecret(parts.Secret, hash).Should().BeTrue();
    }
}
