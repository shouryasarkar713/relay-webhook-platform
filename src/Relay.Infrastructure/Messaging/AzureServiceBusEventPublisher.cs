using System.Text.Json;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Relay.Application.Messaging;

namespace Relay.Infrastructure.Messaging;

public sealed class AzureServiceBusPublisherOptions
{
    public string? ConnectionString { get; set; }
    public string? FullyQualifiedNamespace { get; set; }
    public string QueueOrTopicName { get; set; } = "relay-events";
}

public sealed class AzureServiceBusEventPublisher : IEventMessagePublisher, IAsyncDisposable
{
    private readonly ServiceBusClient _client;
    private readonly ServiceBusSender _sender;
    private readonly ILogger<AzureServiceBusEventPublisher> _logger;

    public AzureServiceBusEventPublisher(
        IOptions<AzureServiceBusPublisherOptions> options,
        ILogger<AzureServiceBusEventPublisher> logger)
    {
        _logger = logger;
        var opt = options.Value;

        if (!string.IsNullOrWhiteSpace(opt.ConnectionString))
        {
            _client = new ServiceBusClient(opt.ConnectionString);
        }
        else if (!string.IsNullOrWhiteSpace(opt.FullyQualifiedNamespace))
        {
            _client = new ServiceBusClient(opt.FullyQualifiedNamespace, new Azure.Identity.DefaultAzureCredential());
        }
        else
        {
            throw new InvalidOperationException(
                "Neither ConnectionString nor FullyQualifiedNamespace was provided in AzureServiceBusPublisherOptions.");
        }

        _sender = _client.CreateSender(opt.QueueOrTopicName);
    }

    public async Task PublishAsync(OutboxEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var eventIdString = envelope.EventId.ToString("D");
        var serializedBody = JsonSerializer.Serialize(envelope);

        var message = new ServiceBusMessage(serializedBody)
        {
            MessageId = eventIdString,
            CorrelationId = eventIdString,
            ContentType = "application/json",
            Subject = envelope.EventType
        };

        // Application metadata properties
        message.ApplicationProperties["EventId"] = eventIdString;
        message.ApplicationProperties["TenantId"] = envelope.TenantId.ToString("D");
        message.ApplicationProperties["EventType"] = envelope.EventType;
        message.ApplicationProperties["OccurredAtUtc"] = envelope.OccurredAtUtc.ToString("O");
        message.ApplicationProperties["SchemaVersion"] = envelope.SchemaVersion;

        _logger.LogDebug(
            "Publishing event {EventId} (Type: {EventType}, Tenant: {TenantId}) to Service Bus",
            envelope.EventId,
            envelope.EventType,
            envelope.TenantId);

        await _sender.SendMessageAsync(message, cancellationToken);

        _logger.LogInformation(
            "Successfully published event {EventId} to Service Bus",
            envelope.EventId);
    }

    public async ValueTask DisposeAsync()
    {
        await _sender.DisposeAsync();
        await _client.DisposeAsync();
    }
}
