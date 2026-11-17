using System.Text.Json;
using Azure.Messaging.ServiceBus;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Relay.Application.Abstractions;
using Relay.Application.Messaging;
using Relay.Domain.Entities;
using Relay.Domain.Enums;
using Relay.Infrastructure;
using Relay.Infrastructure.Data;
using Relay.Infrastructure.Messaging;
using Relay.Infrastructure.Persistence;
using Relay.IntegrationTests.Fixtures;
using Relay.Worker.Options;
using Relay.Worker.Services;
using Xunit;

namespace Relay.IntegrationTests;

[Collection("DatabaseCollection")]
public sealed class AzureServiceBusBrokerIntegrationTests : IAsyncLifetime
{
    private readonly RelayApiFactory _factory;
    private readonly ServiceProvider _serviceProvider;
    private readonly RelayDbContext _dbContext;
    private readonly string _connectionString;
    private readonly string _queueName = "relay-events";
    private readonly bool _isBrokerAvailable;

    public AzureServiceBusBrokerIntegrationTests(RelayApiFactory factory)
    {
        _factory = factory;

        // Check if an Azure Service Bus development namespace or local emulator is available
        _connectionString = Environment.GetEnvironmentVariable("RELAY_SERVICEBUS_CONNECTION_STRING")
            ?? "Endpoint=sb://127.0.0.1;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddDbContext<RelayDbContext>(options =>
        {
            options.UseSqlServer(_factory.ConnectionString, sqlOptions =>
            {
                sqlOptions.EnableRetryOnFailure(3);
            });
        });
        services.AddScoped<IOutboxStore, OutboxStore>();

        services.Configure<AzureServiceBusPublisherOptions>(opts =>
        {
            opts.ConnectionString = _connectionString;
            opts.QueueOrTopicName = _queueName;
        });
        services.AddSingleton<IEventMessagePublisher, AzureServiceBusEventPublisher>();

        _serviceProvider = services.BuildServiceProvider();
        _dbContext = _serviceProvider.GetRequiredService<RelayDbContext>();

        // Probe broker connectivity
        try
        {
            var client = new ServiceBusClient(_connectionString);
            _isBrokerAvailable = true;
        }
        catch
        {
            _isBrokerAvailable = false;
        }
    }

    public async Task InitializeAsync()
    {
        await _dbContext.WebhookDeliveryAttempts.ExecuteDeleteAsync();
        await _dbContext.WebhookDeliveries.ExecuteDeleteAsync();
        await _dbContext.WebhookEventFanouts.ExecuteDeleteAsync();
        await _dbContext.WebhookEndpointSubscriptions.ExecuteDeleteAsync();
        await _dbContext.WebhookEndpoints.ExecuteDeleteAsync();
        await _dbContext.OutboxMessages.ExecuteDeleteAsync();
        await _dbContext.Events.ExecuteDeleteAsync();
    }

    public async Task DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _serviceProvider.DisposeAsync();
    }

    [Fact]
    public async Task RealBroker_SendAndReceive_PreservesPayloadAndMarksPublished()
    {
        if (!_isBrokerAvailable)
        {
            // Skip if no real emulator or broker available
            return;
        }

        // Arrange
        var tenantId = Guid.Parse(RelayApiFactory.TenantAId);
        var eventId = Guid.NewGuid();
        var outboxId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var rawPayload = """{"orderId":"ORD-BROKER-999","amount":2499.50,"currency":"INR"}""";

        var evt = new Event(eventId, tenantId, "order.fulfilled", rawPayload, "fingerprint_broker", "idem_broker_1", now);
        var msg = new OutboxMessage(outboxId, eventId, tenantId, "order.fulfilled", rawPayload, now);
        _dbContext.Events.Add(evt);
        _dbContext.OutboxMessages.Add(msg);
        await _dbContext.SaveChangesAsync();

        var publisher = _serviceProvider.GetRequiredService<IEventMessagePublisher>();
        var store = _serviceProvider.GetRequiredService<IOutboxStore>();
        var options = Options.Create(new OutboxDispatcherOptions
        {
            BatchSize = 10,
            MaxConcurrency = 2,
            LeaseDuration = TimeSpan.FromMinutes(1)
        });

        var dispatcher = new OutboxDispatcher(
            store,
            publisher,
            TimeProvider.System,
            options,
            NullLogger<OutboxDispatcher>.Instance);

        // Act: Process batch with real Azure SDK publishing to Service Bus emulator
        var processed = await dispatcher.ProcessBatchAsync();

        // Assert 1: Outbox row updated in SQL Server
        processed.Should().Be(1);
        var dbRecord = await _dbContext.OutboxMessages.AsNoTracking().FirstAsync(m => m.Id == outboxId);
        dbRecord.Status.Should().Be(OutboxStatus.Published);
        dbRecord.PublishedAtUtc.Should().NotBeNull();
        dbRecord.PublishAttemptCount.Should().Be(1);
        dbRecord.ClaimCount.Should().Be(1);
        dbRecord.LeaseToken.Should().BeNull();

        // Assert 2: Receive message using official Azure Service Bus SDK
        await using var client = new ServiceBusClient(_connectionString);
        var receiver = client.CreateReceiver(_queueName, new ServiceBusReceiverOptions
        {
            ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete
        });

        var receivedMessage = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(15));
        receivedMessage.Should().NotBeNull("Expected message to be received from Azure Service Bus queue");

        // Assert metadata
        receivedMessage!.MessageId.Should().Be(eventId.ToString("D"));
        receivedMessage.CorrelationId.Should().Be(eventId.ToString("D"));
        receivedMessage.ContentType.Should().Be("application/json");
        receivedMessage.Subject.Should().Be("order.fulfilled");

        // Assert application properties
        receivedMessage.ApplicationProperties.Should().ContainKey("TenantId")
            .WhoseValue.Should().Be(tenantId.ToString("D"));
        receivedMessage.ApplicationProperties.Should().ContainKey("EventId")
            .WhoseValue.Should().Be(eventId.ToString("D"));
        receivedMessage.ApplicationProperties.Should().ContainKey("EventType")
            .WhoseValue.Should().Be("order.fulfilled");
        receivedMessage.ApplicationProperties.Should().ContainKey("SchemaVersion")
            .WhoseValue.Should().Be(1);

        // Assert body and payload preservation (no double-escaping)
        var bodyString = receivedMessage.Body.ToString();
        using var jsonDoc = JsonDocument.Parse(bodyString);
        var root = jsonDoc.RootElement;
        root.GetProperty("eventId").GetString().Should().Be(eventId.ToString("D"));
        root.GetProperty("tenantId").GetString().Should().Be(tenantId.ToString("D"));
        root.GetProperty("eventType").GetString().Should().Be("order.fulfilled");
        
        var payloadElement = root.GetProperty("payload");
        payloadElement.ValueKind.Should().Be(JsonValueKind.Object);
        payloadElement.GetProperty("orderId").GetString().Should().Be("ORD-BROKER-999");
        payloadElement.GetProperty("amount").GetDecimal().Should().Be(2499.50m);
        payloadElement.GetProperty("currency").GetString().Should().Be("INR");
    }

    [Fact]
    public async Task RealBroker_DuplicateMessageId_IsDeduplicatedByBroker()
    {
        if (!_isBrokerAvailable)
        {
            return;
        }

        // Arrange
        var testEventId = Guid.NewGuid().ToString("D");
        await using var client = new ServiceBusClient(_connectionString);
        var sender = client.CreateSender(_queueName);
        var receiver = client.CreateReceiver(_queueName, new ServiceBusReceiverOptions
        {
            ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete
        });

        var message1 = new ServiceBusMessage("""{"msg":"first send"}""")
        {
            MessageId = testEventId,
            ContentType = "application/json"
        };
        var message2 = new ServiceBusMessage("""{"msg":"second send duplicate"}""")
        {
            MessageId = testEventId,
            ContentType = "application/json"
        };

        // Act: Send message with identical MessageId twice within the duplicate detection window
        await sender.SendMessageAsync(message1);
        await sender.SendMessageAsync(message2);

        // Assert: Receiver should only receive 1 message because broker deduplicated message2
        var receivedFirst = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        receivedFirst.Should().NotBeNull();
        receivedFirst!.MessageId.Should().Be(testEventId);

        // Attempt second receive with short timeout - queue should be empty
        var receivedSecond = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(3));
        receivedSecond.Should().BeNull("Second message with duplicate MessageId must be suppressed by Service Bus duplicate detection");
    }
}
