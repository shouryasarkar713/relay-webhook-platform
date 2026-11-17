using System.Security.Cryptography;
using System.Text;
using Relay.Application.Abstractions;

namespace Relay.Infrastructure.Services;

public sealed class ApiKeyHasher : IApiKeyHasher
{
    private const string LiveEnvironment = "live";
    private const string TestEnvironment = "test";

    public string HashSecret(string secret)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    public bool VerifySecret(string providedSecret, string expectedHash)
    {
        var providedHash = HashSecret(providedSecret);
        var providedBytes = Encoding.UTF8.GetBytes(providedHash);
        var expectedBytes = Encoding.UTF8.GetBytes(expectedHash);

        return CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }

    public bool TryParseKey(string rawApiKey, out ApiKeyParts parts)
    {
        parts = default;

        if (string.IsNullOrWhiteSpace(rawApiKey))
        {
            return false;
        }

        var trimmed = rawApiKey.Trim();

        string env;
        string remainder;

        if (trimmed.StartsWith("rl_live_", StringComparison.OrdinalIgnoreCase))
        {
            env = LiveEnvironment;
            remainder = trimmed["rl_live_".Length..];
        }
        else if (trimmed.StartsWith("rl_test_", StringComparison.OrdinalIgnoreCase))
        {
            env = TestEnvironment;
            remainder = trimmed["rl_test_".Length..];
        }
        else
        {
            return false;
        }

        var lastUnderscore = remainder.LastIndexOf('_');
        if (lastUnderscore <= 0 || lastUnderscore == remainder.Length - 1)
        {
            return false;
        }

        var prefix = remainder[..lastUnderscore];
        var secret = remainder[(lastUnderscore + 1)..];

        if (string.IsNullOrWhiteSpace(prefix) || string.IsNullOrWhiteSpace(secret))
        {
            return false;
        }

        parts = new ApiKeyParts(env, prefix, secret);
        return true;
    }

    public string GenerateApiKey(string environment, string prefix, out string rawKey)
    {
        var env = environment.ToLowerInvariant();
        if (env != LiveEnvironment && env != TestEnvironment)
        {
            throw new ArgumentException("Environment must be 'live' or 'test'.", nameof(environment));
        }

        var secretBytes = RandomNumberGenerator.GetBytes(32);
        var secret = Convert.ToHexStringLower(secretBytes);

        rawKey = $"rl_{env}_{prefix}_{secret}";
        return HashSecret(secret);
    }
}
