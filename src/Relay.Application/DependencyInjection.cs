using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Relay.Application.Abstractions;
using Relay.Application.Services;

namespace Relay.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<IEventIngestionService, EventIngestionService>();
        services.AddScoped<IEventQueryService, EventQueryService>();
        services.AddScoped<IWebhookEndpointService, WebhookEndpointService>();
        services.AddScoped<IWebhookDeliveryQueryService, WebhookDeliveryQueryService>();
        services.AddSingleton<IWebhookDeliveryFailureClassifier, WebhookDeliveryFailureClassifier>();
        services.AddSingleton<IRetryAfterParser, RetryAfterParser>();
        services.AddSingleton<IWebhookRetryPolicy, WebhookRetryPolicy>();
        services.TryAddSingleton(TimeProvider.System);

        return services;
    }
}
