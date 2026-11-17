using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Relay.Application.Messaging;
using Relay.Domain.Entities;
using Relay.Domain.Enums;
using Relay.Infrastructure;
using Relay.Infrastructure.Messaging;

namespace Relay.Worker.Services;

public sealed class EventFanoutConsumer : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly AzureServiceBusPublisherOptions _options;
    private readonly ILogger<EventFanoutConsumer> _logger;
    private ServiceBusClient? _client;
    private ServiceBusProcessor? _processor;

    public EventFanoutConsumer(
        IServiceProvider serviceProvider,
        IOptions<AzureServiceBusPublisherOptions> options,
        ILogger<EventFanoutConsumer> logger)
    {
        _serviceProvider = serviceProvider;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ConnectionString) && string.IsNullOrWhiteSpace(_options.FullyQualifiedNamespace))
        {
            _logger.LogInformation("EventFanoutConsumer: Service Bus not configured. Consumer disabled.");
            return;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(_options.ConnectionString))
            {
                _client = new ServiceBusClient(_options.ConnectionString);
            }
            else
            {
                _client = new ServiceBusClient(_options.FullyQualifiedNamespace, new Azure.Identity.DefaultAzureCredential());
            }

            _processor = _client.CreateProcessor(_options.QueueOrTopicName, new ServiceBusProcessorOptions
            {
                AutoCompleteMessages = false,
                MaxConcurrentCalls = 10,
                ReceiveMode = ServiceBusReceiveMode.PeekLock
            });

            _processor.ProcessMessageAsync += ProcessMessageAsync;
            _processor.ProcessErrorAsync += ProcessErrorAsync;

            _logger.LogInformation("Starting EventFanoutConsumer on queue {QueueName}...", _options.QueueOrTopicName);
            await _processor.StartProcessingAsync(stoppingToken);

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in EventFanoutConsumer: {Message}", ex.Message);
        }
        finally
        {
            if (_processor is not null)
            {
                await _processor.StopProcessingAsync();
                await _processor.DisposeAsync();
            }

            if (_client is not null)
            {
                await _client.DisposeAsync();
            }
        }
    }

    private async Task ProcessMessageAsync(ProcessMessageEventArgs args)
    {
        var message = args.Message;
        _logger.LogInformation("EventFanoutConsumer received message {MessageId} (Subject: {Subject})", message.MessageId, message.Subject);

        OutboxEnvelope? envelope = null;
        try
        {
            envelope = JsonSerializer.Deserialize<OutboxEnvelope>(message.Body);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Poison message {MessageId}: malformed JSON body. Dead-lettering message.", message.MessageId);
            await args.DeadLetterMessageAsync(message, "MalformedJson", ex.Message, args.CancellationToken);
            return;
        }

        if (envelope is null || envelope.SchemaVersion != 1 || envelope.EventId == Guid.Empty || envelope.TenantId == Guid.Empty)
        {
            _logger.LogError("Poison message {MessageId}: unsupported schema version or missing event IDs. Dead-lettering.", message.MessageId);
            await args.DeadLetterMessageAsync(message, "InvalidSchema", "Unsupported schema version or missing IDs", args.CancellationToken);
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
        var timeProvider = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var now = timeProvider.GetUtcNow();

        try
        {
            var strategy = dbContext.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await dbContext.Database.BeginTransactionAsync(args.CancellationToken);

                // 1. Check fan-out idempotency: Has this EventId already been fanned out?
                var existingFanout = await dbContext.WebhookEventFanouts
                    .FirstOrDefaultAsync(f => f.EventId == envelope.EventId, args.CancellationToken);

                if (existingFanout is not null)
                {
                    _logger.LogInformation(
                        "Event {EventId} has already been fanned out. Skipping duplicate generation.",
                        envelope.EventId);
                    await transaction.CommitAsync(args.CancellationToken);
                    return;
                }

                // 2. Load authoritative Event from database
                var storedEvent = await dbContext.Events
                    .FirstOrDefaultAsync(e => e.Id == envelope.EventId && e.TenantId == envelope.TenantId, args.CancellationToken);

                if (storedEvent is null)
                {
                    _logger.LogWarning(
                        "Event {EventId} for tenant {TenantId} was not found in SQL Server database. Skipping fan-out.",
                        envelope.EventId,
                        envelope.TenantId);
                    await transaction.CommitAsync(args.CancellationToken);
                    return;
                }

                // 3. Find matching active endpoints for this tenant subscribing to this EventType
                var matchingEndpoints = await dbContext.WebhookEndpoints
                    .Include(e => e.Subscriptions)
                    .Where(e => e.TenantId == envelope.TenantId && e.IsActive)
                    .Where(e => e.Subscriptions.Any(s => s.EventType == storedEvent.EventType))
                    .ToListAsync(args.CancellationToken);

                // 4. Create WebhookDelivery records
                var deliveriesCreated = 0;
                foreach (var endpoint in matchingEndpoints)
                {
                    var delivery = new WebhookDelivery(
                        id: Guid.NewGuid(),
                        eventId: storedEvent.Id,
                        endpointId: endpoint.Id,
                        tenantId: storedEvent.TenantId,
                        createdAtUtc: now,
                        status: WebhookDeliveryStatus.Pending);

                    dbContext.WebhookDeliveries.Add(delivery);
                    deliveriesCreated++;
                }

                // 5. Create immutable WebhookEventFanout record
                var fanoutRecord = new WebhookEventFanout(
                    id: Guid.NewGuid(),
                    eventId: storedEvent.Id,
                    tenantId: storedEvent.TenantId,
                    processedAtUtc: now,
                    matchedEndpointCount: deliveriesCreated);

                dbContext.WebhookEventFanouts.Add(fanoutRecord);

                await dbContext.SaveChangesAsync(args.CancellationToken);
                await transaction.CommitAsync(args.CancellationToken);

                _logger.LogInformation(
                    "Fanned out event {EventId} (Type: {EventType}) to {Count} endpoint(s)",
                    storedEvent.Id,
                    storedEvent.EventType,
                    deliveriesCreated);
            });

            // Settle broker message ONLY AFTER successful database commit
            await args.CompleteMessageAsync(message, args.CancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transient error processing fanout for event {EventId}. Abandoning message for redelivery.", envelope.EventId);
            await args.AbandonMessageAsync(message, cancellationToken: args.CancellationToken);
        }
    }

    private Task ProcessErrorAsync(ProcessErrorEventArgs args)
    {
        _logger.LogError(args.Exception, "Service Bus error in EventFanoutConsumer: {ErrorSource} - {Message}", args.ErrorSource, args.Exception.Message);
        return Task.CompletedTask;
    }
}
