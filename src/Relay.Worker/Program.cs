using Azure.Monitor.OpenTelemetry.AspNetCore;
using Relay.Application;
using Relay.Application.Messaging;
using Relay.Infrastructure;
using Relay.Infrastructure.Messaging;
using Relay.Worker.Options;
using Relay.Worker.Services;

var builder = Host.CreateApplicationBuilder(args);

// Configure Azure Monitor / OpenTelemetry when connection string is provided
var appInsightsConnection = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]
    ?? builder.Configuration["AzureMonitor:ConnectionString"];

if (!string.IsNullOrWhiteSpace(appInsightsConnection))
{
    builder.Services.AddOpenTelemetry().UseAzureMonitor(options =>
    {
        options.ConnectionString = appInsightsConnection;
    });
}

// Register TimeProvider
builder.Services.AddSingleton(TimeProvider.System);

// Register application and infrastructure layers
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// Configure Outbox Dispatcher options
builder.Services.Configure<OutboxDispatcherOptions>(
    builder.Configuration.GetSection(OutboxDispatcherOptions.SectionName));

// Configure Azure Service Bus options
builder.Services.Configure<AzureServiceBusPublisherOptions>(
    builder.Configuration.GetSection("ServiceBus"));

// Register Event Publisher
var serviceBusConnection = builder.Configuration["ServiceBus:ConnectionString"];
var serviceBusNamespace = builder.Configuration["ServiceBus:FullyQualifiedNamespace"];
var allowSimulationPublisher = builder.Configuration.GetValue<bool>("ServiceBus:AllowSimulationPublisher");

if (!string.IsNullOrWhiteSpace(serviceBusConnection) || !string.IsNullOrWhiteSpace(serviceBusNamespace))
{
    builder.Services.AddSingleton<IEventMessagePublisher, AzureServiceBusEventPublisher>();
}
else if (builder.Environment.IsDevelopment() && allowSimulationPublisher)
{
    // Explicit opt-in simulation publisher for diagnostic local debugging only
    builder.Services.AddSingleton<IEventMessagePublisher, SimulationDiagnosticPublisher>();
}
else
{
    // Strict production guard: Fail fast on startup if broker is unconfigured.
    // Never silently discard accepted events or masquerade simulation as successful delivery.
    throw new InvalidOperationException(
        "Critical Configuration Error: Azure Service Bus is not configured. " +
        "You must configure either 'ServiceBus:ConnectionString' or 'ServiceBus:FullyQualifiedNamespace'. " +
        "For local development testing without a broker, you must explicitly set 'ServiceBus:AllowSimulationPublisher=true' " +
        "in Development environment.");
}

// Configure HttpClient with SSRF-protected SocketsHttpHandler
builder.Services.AddHttpClient("SecureWebhookClient")
    .ConfigurePrimaryHttpMessageHandler(sp =>
    {
        var securityOptions = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Relay.Infrastructure.Security.WebhookSecurityOptions>>();
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("SecureWebhookClient");
        return Relay.Infrastructure.Security.SecureSocketsHttpHandlerFactory.Create(securityOptions, logger);
    });

// Register Outbox Dispatcher, Fanout Consumer, and Delivery Background Services
builder.Services.AddScoped<OutboxDispatcher>();
builder.Services.AddHostedService<OutboxBackgroundService>();

builder.Services.AddHostedService<EventFanoutConsumer>();

builder.Services.AddScoped<WebhookDeliveryDispatcher>();
builder.Services.AddHostedService<WebhookDeliveryBackgroundService>();

var host = builder.Build();
host.Run();

// Explicit diagnostic simulation publisher: Throws NotSupportedException on publish attempts unless in diagnostic mode
internal sealed class SimulationDiagnosticPublisher : IEventMessagePublisher
{
    private readonly ILogger<SimulationDiagnosticPublisher> _logger;

    public SimulationDiagnosticPublisher(ILogger<SimulationDiagnosticPublisher> logger)
    {
        _logger = logger;
        _logger.LogWarning(
            "*** WARNING: SimulationDiagnosticPublisher is active. Events are NOT being delivered to any message broker! ***");
    }

    public Task PublishAsync(OutboxEnvelope envelope, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning(
            "[SIMULATION ONLY - NO BROKER] Simulated publish for event {EventId} (Type: {EventType}, Tenant: {TenantId})",
            envelope.EventId,
            envelope.EventType,
            envelope.TenantId);

        return Task.CompletedTask;
    }
}
