using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Relay.Application.Abstractions;
using Relay.Infrastructure.Services;

namespace Relay.Infrastructure;

public static class DependencyInjection
{
    public const string DefaultConnectionName = "RelayDatabase";

    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(DefaultConnectionName)
            ?? "Server=localhost,1433;Database=RelayDb;User Id=sa;Password=Your_Strong_Password123!;TrustServerCertificate=True;MultipleActiveResultSets=True;";

        services.AddDbContext<RelayDbContext>(options =>
        {
            options.UseSqlServer(connectionString, sqlOptions =>
            {
                sqlOptions.MigrationsAssembly(typeof(RelayDbContext).Assembly.FullName);
                sqlOptions.EnableRetryOnFailure(
                    maxRetryCount: 5,
                    maxRetryDelay: TimeSpan.FromSeconds(30),
                    errorNumbersToAdd: null);
            });
        });

        services.AddScoped<IRelayDbContext>(sp => sp.GetRequiredService<RelayDbContext>());
        services.AddScoped<IOutboxStore, Persistence.OutboxStore>();
        services.AddScoped<IWebhookDeliveryStore, Persistence.WebhookDeliveryStore>();
        services.AddSingleton<IApiKeyHasher, ApiKeyHasher>();
        services.AddSingleton<IWebhookSigner, Security.WebhookSigner>();
        services.AddSingleton<IWebhookUrlValidator, Security.WebhookUrlValidator>();
        services.AddSingleton<IEndpointSecretProtector, Security.DataProtectionEndpointSecretProtector>();
        services.AddScoped<IEndpointCircuitBreakerManager, Services.EndpointCircuitBreakerManager>();
        services.AddSingleton<IBulkheadManager, Services.EndpointBulkheadManager>();

        // Configure WebhookSecurityOptions & ResilienceOptions
        services.Configure<Security.WebhookSecurityOptions>(options =>
        {
            configuration.GetSection(Security.WebhookSecurityOptions.SectionName).Bind(options);
        });
        services.Configure<Application.Options.DeliveryRetryOptions>(options =>
        {
            configuration.GetSection(Application.Options.DeliveryRetryOptions.SectionName).Bind(options);
        });
        services.Configure<Application.Options.CircuitBreakerOptions>(options =>
        {
            configuration.GetSection(Application.Options.CircuitBreakerOptions.SectionName).Bind(options);
        });
        services.Configure<Application.Options.BulkheadOptions>(options =>
        {
            configuration.GetSection(Application.Options.BulkheadOptions.SectionName).Bind(options);
        });

        // Configure Data Protection (Cloud Blob + Key Vault in Production, Local FileSystem in Dev/Test)
        var dpBuilder = services.AddDataProtection()
            .SetApplicationName("Relay.Platform");

        var blobUri = configuration["DataProtection:BlobUri"];
        var keyVaultKeyId = configuration["DataProtection:KeyVaultKeyIdentifier"];

        if (!string.IsNullOrWhiteSpace(blobUri))
        {
            var credential = new Azure.Identity.DefaultAzureCredential();
            dpBuilder.PersistKeysToAzureBlobStorage(new Uri(blobUri), credential);

            if (!string.IsNullOrWhiteSpace(keyVaultKeyId))
            {
                dpBuilder.ProtectKeysWithAzureKeyVault(new Uri(keyVaultKeyId), credential);
            }
        }
        else
        {
            var keysPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".relay", "dataprotection-keys");
            dpBuilder.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
        }

        return services;
    }
}
