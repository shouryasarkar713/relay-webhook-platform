namespace Relay.Domain.Entities;

public sealed class ApiKey
{
    private ApiKey() { }

    public ApiKey(
        Guid id,
        Guid tenantId,
        string name,
        string keyPrefix,
        string keyHash,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? expiresAtUtc = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("API key name cannot be empty.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(keyPrefix))
        {
            throw new ArgumentException("API key prefix cannot be empty.", nameof(keyPrefix));
        }

        if (string.IsNullOrWhiteSpace(keyHash))
        {
            throw new ArgumentException("API key hash cannot be empty.", nameof(keyHash));
        }

        Id = id;
        TenantId = tenantId;
        Name = name.Trim();
        KeyPrefix = keyPrefix.Trim();
        KeyHash = keyHash.Trim();
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string KeyPrefix { get; private set; } = string.Empty;
    public string KeyHash { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? LastUsedAtUtc { get; private set; }
    public DateTimeOffset? ExpiresAtUtc { get; private set; }
    public DateTimeOffset? RevokedAtUtc { get; private set; }

    public Tenant? Tenant { get; private set; }

    public bool IsRevoked => RevokedAtUtc.HasValue;

    public bool IsExpired(DateTimeOffset now) => ExpiresAtUtc.HasValue && ExpiresAtUtc.Value <= now;

    public bool IsValid(DateTimeOffset now) => !IsRevoked && !IsExpired(now);

    public void Revoke(DateTimeOffset revokedAtUtc)
    {
        if (!RevokedAtUtc.HasValue)
        {
            RevokedAtUtc = revokedAtUtc;
        }
    }

    public void RecordUsage(DateTimeOffset usedAtUtc)
    {
        LastUsedAtUtc = usedAtUtc;
    }
}
