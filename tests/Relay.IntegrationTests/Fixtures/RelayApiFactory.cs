using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Relay.Application.Abstractions;
using Relay.Domain.Entities;
using Relay.Infrastructure;

namespace Relay.IntegrationTests.Fixtures;

public class RelayApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string TenantAId = "11111111-1111-1111-1111-111111111111";
    public const string TenantBId = "22222222-2222-2222-2222-222222222222";

    public const string KeyASecret = "1111111122222222333333334444444455555555666666667777777788888888";
    public const string KeyBSecret = "aaaaaaaabbbbbbbbccccccccddddddddeeeeeeeeffffffff0000000011111111";
    public const string RevokedKeySecret = "revokedrevokedrevokedrevokedrevokedrevokedrevokedrevokedrevoked1";
    public const string ExpiredKeySecret = "expiredexpiredexpiredexpiredexpiredexpiredexpiredexpiredexpired1";
    public const string InactiveTenantSecret = "inactivetenantinactivetenantinactivetenantinactivetenantinactive1";

    public const string ApiKeyA = $"rl_live_pk_test_a_{KeyASecret}";
    public const string ApiKeyB = $"rl_live_pk_test_b_{KeyBSecret}";
    public const string ApiKeyRevoked = $"rl_live_pk_test_revoked_{RevokedKeySecret}";
    public const string ApiKeyExpired = $"rl_live_pk_test_expired_{ExpiredKeySecret}";
    public const string ApiKeyInactive = $"rl_live_pk_test_inactive_{InactiveTenantSecret}";

    private readonly string _connectionString;

    public RelayApiFactory()
    {
        _connectionString = Environment.GetEnvironmentVariable("RELAY_TEST_CONNECTION_STRING")
            ?? "Server=localhost,1433;Database=RelayTestDb;User Id=sa;Password=Your_Strong_Password123!;TrustServerCertificate=True;MultipleActiveResultSets=True;";
    }

    public string ConnectionString => _connectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:RelayDatabase"] = _connectionString
            });
        });

        builder.ConfigureServices(services =>
        {
            // Replace existing DbContext registration with test connection string
            var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<RelayDbContext>));
            if (descriptor is not null)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<RelayDbContext>(options =>
            {
                options.UseSqlServer(_connectionString, sqlOptions =>
                {
                    sqlOptions.MigrationsAssembly(typeof(RelayDbContext).Assembly.FullName);
                    sqlOptions.EnableRetryOnFailure(3);
                });
            });
        });
    }

    public async Task InitializeAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IApiKeyHasher>();
        var timeProvider = scope.ServiceProvider.GetRequiredService<TimeProvider>();

        await db.Database.MigrateAsync();
        await ResetAndSeedDataAsync(db, hasher, timeProvider);
    }

    public static async Task ResetAndSeedDataAsync(RelayDbContext db, IApiKeyHasher hasher, TimeProvider timeProvider)
    {
        var now = timeProvider.GetUtcNow();

        // Clear existing test data
        await db.OutboxMessages.ExecuteDeleteAsync();
        await db.Events.ExecuteDeleteAsync();
        await db.ApiKeys.ExecuteDeleteAsync();
        await db.Tenants.ExecuteDeleteAsync();

        // 1. Tenant A
        var tenantA = new Tenant(Guid.Parse(TenantAId), "Tenant Alpha", "tenant-alpha", now, isActive: true);
        var keyA = new ApiKey(
            id: Guid.NewGuid(),
            tenantId: tenantA.Id,
            name: "Alpha Production Key",
            keyPrefix: "pk_test_a",
            keyHash: hasher.HashSecret(KeyASecret),
            createdAtUtc: now);

        // Revoked key for Tenant A
        var revokedKey = new ApiKey(
            id: Guid.NewGuid(),
            tenantId: tenantA.Id,
            name: "Alpha Revoked Key",
            keyPrefix: "pk_test_revoked",
            keyHash: hasher.HashSecret(RevokedKeySecret),
            createdAtUtc: now);
        revokedKey.Revoke(now);

        // Expired key for Tenant A
        var expiredKey = new ApiKey(
            id: Guid.NewGuid(),
            tenantId: tenantA.Id,
            name: "Alpha Expired Key",
            keyPrefix: "pk_test_expired",
            keyHash: hasher.HashSecret(ExpiredKeySecret),
            createdAtUtc: now.AddDays(-10),
            expiresAtUtc: now.AddDays(-1));

        // 2. Tenant B
        var tenantB = new Tenant(Guid.Parse(TenantBId), "Tenant Beta", "tenant-beta", now, isActive: true);
        var keyB = new ApiKey(
            id: Guid.NewGuid(),
            tenantId: tenantB.Id,
            name: "Beta Production Key",
            keyPrefix: "pk_test_b",
            keyHash: hasher.HashSecret(KeyBSecret),
            createdAtUtc: now);

        // 3. Inactive Tenant
        var inactiveTenant = new Tenant(Guid.NewGuid(), "Inactive Tenant", "inactive-tenant", now, isActive: false);
        var keyInactive = new ApiKey(
            id: Guid.NewGuid(),
            tenantId: inactiveTenant.Id,
            name: "Inactive Key",
            keyPrefix: "pk_test_inactive",
            keyHash: hasher.HashSecret(InactiveTenantSecret),
            createdAtUtc: now);

        db.Tenants.AddRange(tenantA, tenantB, inactiveTenant);
        db.ApiKeys.AddRange(keyA, revokedKey, expiredKey, keyB, keyInactive);

        await db.SaveChangesAsync();
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
    }
}
