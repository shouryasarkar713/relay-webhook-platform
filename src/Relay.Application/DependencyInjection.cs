using Microsoft.Extensions.DependencyInjection;
using Relay.Application.Services;

namespace Relay.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<IEventIngestionService, EventIngestionService>();
        services.AddScoped<IEventQueryService, EventQueryService>();
        services.AddSingleton(TimeProvider.System);

        return services;
    }
}
