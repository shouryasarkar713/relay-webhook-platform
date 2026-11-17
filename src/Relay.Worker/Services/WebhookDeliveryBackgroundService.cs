using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Relay.Worker.Options;

namespace Relay.Worker.Services;

public sealed class WebhookDeliveryBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly OutboxDispatcherOptions _options;
    private readonly ILogger<WebhookDeliveryBackgroundService> _logger;

    public WebhookDeliveryBackgroundService(
        IServiceProvider serviceProvider,
        IOptions<OutboxDispatcherOptions> options,
        ILogger<WebhookDeliveryBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("WebhookDeliveryBackgroundService started.");

        using var timer = new PeriodicTimer(_options.PollingInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<WebhookDeliveryDispatcher>();

                int processedCount;
                do
                {
                    processedCount = await dispatcher.ProcessDeliveriesAsync(stoppingToken);
                }
                while (processedCount >= _options.BatchSize && !stoppingToken.IsCancellationRequested);

                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in WebhookDeliveryBackgroundService: {Message}", ex.Message);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        _logger.LogInformation("WebhookDeliveryBackgroundService stopped.");
    }
}
