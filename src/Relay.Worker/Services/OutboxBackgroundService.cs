using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Relay.Worker.Options;

namespace Relay.Worker.Services;

public sealed class OutboxBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly OutboxDispatcherOptions _options;
    private readonly ILogger<OutboxBackgroundService> _logger;

    public OutboxBackgroundService(
        IServiceProvider serviceProvider,
        IOptions<OutboxDispatcherOptions> options,
        ILogger<OutboxBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _options.Validate();
        _logger.LogInformation(
            "Outbox Background Service started. PollingInterval={Interval}s, BatchSize={BatchSize}, Lease={Lease}s, Concurrency={Concurrency}",
            _options.PollingInterval.TotalSeconds,
            _options.BatchSize,
            _options.LeaseDuration.TotalSeconds,
            _options.MaxConcurrency);

        using var periodicTimer = new PeriodicTimer(_options.PollingInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<OutboxDispatcher>();

                int processedCount;
                do
                {
                    processedCount = await dispatcher.ProcessBatchAsync(stoppingToken);
                }
                while (processedCount >= _options.BatchSize && !stoppingToken.IsCancellationRequested);

                await periodicTimer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in Outbox Background Service tick: {Message}", ex.Message);
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

        _logger.LogInformation("Outbox Background Service stopped.");
    }
}
