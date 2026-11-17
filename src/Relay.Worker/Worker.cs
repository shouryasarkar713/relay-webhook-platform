namespace Relay.Worker;

public sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;

    public Worker(ILogger<Worker> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Relay Background Worker Service started. (Phase 2 Outbox Dispatcher is pending activation)");

        while (!stoppingToken.IsCancellationRequested)
        {
            // Heartbeat idle loop until Phase 2 dispatcher is wired up
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }

        _logger.LogInformation("Relay Background Worker Service stopped.");
    }
}
