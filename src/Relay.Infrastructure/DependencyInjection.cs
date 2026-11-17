using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
        services.AddSingleton<IApiKeyHasher, ApiKeyHasher>();

        return services;
    }
}
