namespace Relay.Application.Abstractions;

public readonly record struct ApiKeyParts(string Environment, string Prefix, string Secret);

public interface IApiKeyHasher
{
    string HashSecret(string secret);
    bool VerifySecret(string providedSecret, string expectedHash);
    bool TryParseKey(string rawApiKey, out ApiKeyParts parts);
    string GenerateApiKey(string environment, string prefix, out string rawKey);
}
