using System.Security.Cryptography;
using System.Text;
using Relay.Application.Abstractions;

namespace Relay.Infrastructure.Security;

public sealed class WebhookSigner : IWebhookSigner
{
    public WebhookSignature Sign(string signingSecret, byte[] bodyBytes, long? fixedTimestamp = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signingSecret);
        ArgumentNullException.ThrowIfNull(bodyBytes);

        var timestamp = fixedTimestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var timestampBytes = Encoding.UTF8.GetBytes($"{timestamp}.");

        // Canonical payload = <unix-timestamp>.<exact-raw-body-bytes>
        var canonicalBytes = new byte[timestampBytes.Length + bodyBytes.Length];
        Buffer.BlockCopy(timestampBytes, 0, canonicalBytes, 0, timestampBytes.Length);
        Buffer.BlockCopy(bodyBytes, 0, canonicalBytes, timestampBytes.Length, bodyBytes.Length);

        var secretBytes = Encoding.UTF8.GetBytes(signingSecret);
        using var hmac = new HMACSHA256(secretBytes);
        var hash = hmac.ComputeHash(canonicalBytes);
        var signatureHex = Convert.ToHexString(hash).ToLowerInvariant();

        var headerValue = $"t={timestamp},v1={signatureHex}";
        return new WebhookSignature(timestamp, signatureHex, headerValue);
    }

    public bool Verify(
        string signingSecret,
        byte[] bodyBytes,
        string signatureHeader,
        TimeSpan tolerance,
        long? currentTimestamp = null)
    {
        if (string.IsNullOrWhiteSpace(signingSecret) || bodyBytes == null || string.IsNullOrWhiteSpace(signatureHeader))
        {
            return false;
        }

        // Parse header format: t=<timestamp>,v1=<signature>
        var parts = signatureHeader.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        long? parsedTimestamp = null;
        string? parsedSignature = null;

        foreach (var part in parts)
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;

            if (kv[0] == "t" && long.TryParse(kv[1], out var t))
            {
                parsedTimestamp = t;
            }
            else if (kv[0] == "v1")
            {
                parsedSignature = kv[1];
            }
        }

        if (!parsedTimestamp.HasValue || string.IsNullOrWhiteSpace(parsedSignature))
        {
            return false;
        }

        // Verify timestamp tolerance against replay attacks
        var nowSeconds = currentTimestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var diff = Math.Abs(nowSeconds - parsedTimestamp.Value);
        if (diff > tolerance.TotalSeconds)
        {
            return false;
        }

        // Recompute expected signature
        var expected = Sign(signingSecret, bodyBytes, parsedTimestamp.Value);

        // Constant-time comparison
        var expectedBytes = Encoding.UTF8.GetBytes(expected.SignatureHex);
        var actualBytes = Encoding.UTF8.GetBytes(parsedSignature.ToLowerInvariant());

        return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
}
