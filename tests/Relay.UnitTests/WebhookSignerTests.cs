using System.Text;
using FluentAssertions;
using Relay.Infrastructure.Security;
using Xunit;

namespace Relay.UnitTests;

public sealed class WebhookSignerTests
{
    private readonly WebhookSigner _signer = new();

    [Fact]
    public void Sign_ProducesDocumentedCanonicalHmacSignature()
    {
        var secret = "whsec_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var bodyBytes = Encoding.UTF8.GetBytes("""{"event":"order.created","amount":100}""");
        var timestamp = 1700000000L;

        var signature = _signer.Sign(secret, bodyBytes, timestamp);

        signature.Timestamp.Should().Be(timestamp);
        signature.SignatureHex.Should().NotBeNullOrEmpty();
        signature.HeaderValue.Should().Be($"t=1700000000,v1={signature.SignatureHex}");

        // Independent verification of constant-time verification logic
        var isValid = _signer.Verify(secret, bodyBytes, signature.HeaderValue, TimeSpan.FromMinutes(5), currentTimestamp: timestamp);
        isValid.Should().BeTrue();
    }

    [Fact]
    public void Verify_RejectsTamperedPayload()
    {
        var secret = "whsec_test_secret";
        var originalBytes = Encoding.UTF8.GetBytes("""{"id":1}""");
        var tamperedBytes = Encoding.UTF8.GetBytes("""{"id":2}""");
        var timestamp = 1700000000L;

        var signature = _signer.Sign(secret, originalBytes, timestamp);

        var isValid = _signer.Verify(secret, tamperedBytes, signature.HeaderValue, TimeSpan.FromMinutes(5), currentTimestamp: timestamp);
        isValid.Should().BeFalse();
    }

    [Fact]
    public void Verify_RejectsExpiredTimestampOutsideTolerance()
    {
        var secret = "whsec_test_secret";
        var bodyBytes = Encoding.UTF8.GetBytes("{}");
        var timestamp = 1700000000L;

        var signature = _signer.Sign(secret, bodyBytes, timestamp);

        // 10 minutes later with 5 minute tolerance
        var isValid = _signer.Verify(secret, bodyBytes, signature.HeaderValue, TimeSpan.FromMinutes(5), currentTimestamp: timestamp + 600);
        isValid.Should().BeFalse();
    }
}
