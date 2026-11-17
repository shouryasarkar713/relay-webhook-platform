using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Relay.Application.Abstractions;
using Relay.Domain.Entities;

namespace Relay.Infrastructure.Data;

public static class DbInitializer
{
    public const string DefaultDevTenantSlug = "acme-corp";
    public const string DefaultDevKeyPrefix = "pk_dev001";

    public static async Task<(Tenant Tenant, string? GeneratedRawKey)> EnsureDevelopmentTenantSeededAsync(
        RelayDbContext dbContext,
        IApiKeyHasher apiKeyHasher,
        TimeProvider timeProvider,
        ILogger logger,
        string? specifiedDevSecret = null,
        CancellationToken cancellationToken = default)
    {
        // 1. Explicitly revoke the compromised historical dev key if present
        var compromisedHistoricalKey = await dbContext.ApiKeys
            .FirstOrDefaultAsync(k => k.KeyPrefix == "pk_dev001" && k.RevokedAtUtc == null, cancellationToken);
        if (compromisedHistoricalKey is not null)
        {
            compromisedHistoricalKey.Revoke(timeProvider.GetUtcNow());
            logger.LogWarning("Revoked compromised legacy development key prefix {Prefix}", "pk_dev001");
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var existingTenant = await dbContext.Tenants
            .Include(t => t.ApiKeys)
            .FirstOrDefaultAsync(t => t.Slug == DefaultDevTenantSlug, cancellationToken);

        if (existingTenant is not null)
        {
            var activeKey = existingTenant.ApiKeys.FirstOrDefault(k => k.IsValid(timeProvider.GetUtcNow()));
            if (activeKey is null)
            {
                // Generate a fresh key for the existing tenant
                var newPrefix = $"pk_{Guid.NewGuid():N}"[..12];
                string newRawKey;
                string newKeyHash;
                if (!string.IsNullOrWhiteSpace(specifiedDevSecret))
                {
                    newKeyHash = apiKeyHasher.HashSecret(specifiedDevSecret);
                    newRawKey = $"rl_live_{newPrefix}_{specifiedDevSecret}";
                }
                else
                {
                    newKeyHash = apiKeyHasher.GenerateApiKey("live", newPrefix, out newRawKey);
                }

                var freshApiKey = new ApiKey(
                    id: Guid.NewGuid(),
                    tenantId: existingTenant.Id,
                    name: "Fresh Dev Key",
                    keyPrefix: newPrefix,
                    keyHash: newKeyHash,
                    createdAtUtc: timeProvider.GetUtcNow());

                dbContext.ApiKeys.Add(freshApiKey);
                await dbContext.SaveChangesAsync(cancellationToken);
                logger.LogInformation("Generated fresh API key with prefix {Prefix} for existing tenant {Slug}", newPrefix, DefaultDevTenantSlug);
                return (existingTenant, newRawKey);
            }

            return (existingTenant, null);
        }

        var now = timeProvider.GetUtcNow();
        var tenantId = Guid.NewGuid();
        var tenant = new Tenant(
            id: tenantId,
            name: "Acme Corporation (Dev)",
            slug: DefaultDevTenantSlug,
            createdAtUtc: now,
            isActive: true);

        string rawKey;
        string keyHash;

        if (!string.IsNullOrWhiteSpace(specifiedDevSecret))
        {
            keyHash = apiKeyHasher.HashSecret(specifiedDevSecret);
            rawKey = $"rl_live_{DefaultDevKeyPrefix}_{specifiedDevSecret}";
        }
        else
        {
            keyHash = apiKeyHasher.GenerateApiKey("live", DefaultDevKeyPrefix, out rawKey);
        }

        var apiKey = new ApiKey(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            name: "Default Dev Key",
            keyPrefix: DefaultDevKeyPrefix,
            keyHash: keyHash,
            createdAtUtc: now);

        dbContext.Tenants.Add(tenant);
        dbContext.ApiKeys.Add(apiKey);

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Seeded development tenant {Slug} with key prefix {Prefix}", DefaultDevTenantSlug, DefaultDevKeyPrefix);

        return (tenant, rawKey);
    }
}
