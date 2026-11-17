using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Relay.Application;
using Relay.Application.Abstractions;
using Relay.Application.DTOs;
using Relay.Application.Messaging;
using Relay.Application.Options;
using Relay.Domain.Entities;
using Relay.Domain.Enums;
using Relay.Infrastructure;
using Relay.Infrastructure.Messaging;
using Relay.Infrastructure.Persistence;
using Relay.Infrastructure.Security;
using Relay.Infrastructure.Services;
using Relay.IntegrationTests.Fixtures;
using Relay.Worker.Options;
using Relay.Worker.Services;
using Xunit;

namespace Relay.IntegrationTests;

[Collection("DatabaseCollection")]
public sealed class EndToEndDeliveryIntegrationTests : IAsyncLifetime
{
    private readonly RelayApiFactory _factory;
    private readonly HttpClient _apiClient;
    private readonly ServiceProvider _serviceProvider;
    private readonly RelayDbContext _dbContext;
    private readonly string _serviceBusConnectionString;
    private readonly string _queueName = "relay-events";

    public EndToEndDeliveryIntegrationTests(RelayApiFactory factory)
    {
        _factory = factory;
        _apiClient = _factory.CreateClient();
        _apiClient.DefaultRequestHeaders.Add("X-Api-Key", RelayApiFactory.ApiKeyA);

        _serviceBusConnectionString = Environment.GetEnvironmentVariable("RELAY_SERVICEBUS_CONNECTION_STRING")
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

        // Application Services & Repositories
        services.AddScoped<IOutboxStore, OutboxStore>();
        services.AddScoped<IWebhookDeliveryStore, WebhookDeliveryStore>();
        services.AddSingleton<IWebhookSigner, WebhookSigner>();
        services.AddSingleton<IEndpointSecretProtector, DataProtectionEndpointSecretProtector>();

        services.AddApplication();
        services.AddScoped<IEndpointCircuitBreakerManager, EndpointCircuitBreakerManager>();
        services.AddSingleton<IBulkheadManager, EndpointBulkheadManager>();

        services.Configure<OutboxDispatcherOptions>(opt =>
        {
            opt.BatchSize = 10;
            opt.MaxConcurrency = 1;
            opt.LeaseDuration = TimeSpan.FromMinutes(1);
        });

        services.Configure<DeliveryRetryOptions>(opt =>
        {
            opt.MaxAttempts = 5;
            opt.BaseDelaySeconds = 1;
            opt.MaxDelaySeconds = 60;
            opt.MaxRetryWindowHours = 24;
        });

        services.Configure<CircuitBreakerOptions>(opt =>
        {
            opt.FailureThreshold = 5;
            opt.OpenDurationSeconds = 30;
        });

        services.Configure<BulkheadOptions>(opt =>
        {
            opt.MaxGlobalConcurrency = 50;
            opt.MaxPerEndpointConcurrency = 5;
        });

        services.AddScoped<WebhookDeliveryDispatcher>();

        var keysPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".relay", "dataprotection-keys");
        services.AddDataProtection()
            .SetApplicationName("Relay.Platform")
            .PersistKeysToFileSystem(new DirectoryInfo(keysPath));

        services.Configure<AzureServiceBusPublisherOptions>(opts =>
        {
            opts.ConnectionString = _serviceBusConnectionString;
            opts.QueueOrTopicName = _queueName;
        });
        services.AddSingleton<IEventMessagePublisher, AzureServiceBusEventPublisher>();

        services.Configure<WebhookSecurityOptions>(opt =>
        {
            opt.RequireHttps = false;
            opt.AllowLoopbackForTesting = true;
            opt.RequestTimeout = TimeSpan.FromSeconds(5);
        });

        services.AddHttpClient("SecureWebhookClient")
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var secOptions = sp.GetRequiredService<IOptions<WebhookSecurityOptions>>();
                return SecureSocketsHttpHandlerFactory.Create(secOptions, NullLogger.Instance);
            });

        _serviceProvider = services.BuildServiceProvider();
        _dbContext = _serviceProvider.GetRequiredService<RelayDbContext>();
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

        // Drain any lingering messages from queue
        try
        {
            await using var client = new ServiceBusClient(_serviceBusConnectionString);
            var receiver = client.CreateReceiver(_queueName, new ServiceBusReceiverOptions
            {
                ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete
            });
            while (await receiver.ReceiveMessageAsync(TimeSpan.FromMilliseconds(300)) is not null)
            {
            }
        }
        catch
        {
            // Ignore if broker unavailable during init
        }
    }

    public async Task DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _serviceProvider.DisposeAsync();
    }

    [Fact]
    public async Task EndToEnd_IngestToFanoutToDelivery_DeliversSignedPayloadSuccessfully()
    {
        // Step 1: Start local test HTTP receiver
        string? receivedSignature = null;
        string? receivedEventId = null;
        string? receivedDeliveryId = null;
        byte[]? receivedBody = null;

        using var receiverHost = await StartTestReceiverAsync(async context =>
        {
            receivedSignature = context.Request.Headers["X-Relay-Signature"];
            receivedEventId = context.Request.Headers["X-Relay-Event-Id"];
            receivedDeliveryId = context.Request.Headers["X-Relay-Delivery-Id"];

            using var ms = new MemoryStream();
            await context.Request.Body.CopyToAsync(ms);
            receivedBody = ms.ToArray();

            context.Response.StatusCode = 200;
            await context.Response.WriteAsync("""{"acknowledged":true}""");
        });

        var receiverUrl = receiverHost.GetListeningUrl();

        // Step 2: Register a webhook endpoint via API
        var createEpRequest = new CreateWebhookEndpointRequest(
            Name: "Customer Invoicing Webhook",
            Url: receiverUrl,
            EventTypes: new[] { "invoice.paid" });

        var epResponse = await _apiClient.PostAsJsonAsync("/v1/endpoints", createEpRequest);
        epResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var createdEp = await epResponse.Content.ReadFromJsonAsync<WebhookEndpointResponse>();
        createdEp.Should().NotBeNull();
        createdEp!.SigningSecret.Should().NotBeNullOrEmpty();
        var rawSigningSecret = createdEp.SigningSecret!;

        // Step 3: Ingest an event via POST /v1/events
        var ingestRequest = new IngestEventRequest(
            "invoice.paid",
            JsonSerializer.Deserialize<JsonElement>("""{"invoiceId":"INV-9001","amount":1500.00,"currency":"USD"}"""));

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/events")
        {
            Content = JsonContent.Create(ingestRequest)
        };
        httpRequest.Headers.Add("Idempotency-Key", $"e2e_idem_{Guid.NewGuid():N}");

        var ingestResponse = await _apiClient.SendAsync(httpRequest);
        ingestResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var ingestResult = await ingestResponse.Content.ReadFromJsonAsync<IngestEventResponse>();
        ingestResult.Should().NotBeNull();
        var eventId = ingestResult!.EventId;

        // Step 4: Dispatch Outbox message to Azure Service Bus Emulator
        var outboxStore = _serviceProvider.GetRequiredService<IOutboxStore>();
        var publisher = _serviceProvider.GetRequiredService<IEventMessagePublisher>();
        var outboxDispatcher = new OutboxDispatcher(
            outboxStore,
            publisher,
            TimeProvider.System,
            Options.Create(new OutboxDispatcherOptions { BatchSize = 10, MaxConcurrency = 1 }),
            NullLogger<OutboxDispatcher>.Instance);

        var publishedCount = await outboxDispatcher.ProcessBatchAsync();
        publishedCount.Should().Be(1);

        var outboxMsg = await _dbContext.OutboxMessages.FirstAsync(m => m.EventId == eventId);
        outboxMsg.Status.Should().Be(OutboxStatus.Published);

        // Step 5: Receive published message from Azure Service Bus Emulator & Execute Fanout
        await using var sbClient = new ServiceBusClient(_serviceBusConnectionString);
        var receiver = sbClient.CreateReceiver(_queueName, new ServiceBusReceiverOptions
        {
            ReceiveMode = ServiceBusReceiveMode.PeekLock
        });

        var sbMessage = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
        sbMessage.Should().NotBeNull("Service bus message should be available in queue");

        var envelope = JsonSerializer.Deserialize<OutboxEnvelope>(
            Encoding.UTF8.GetString(sbMessage!.Body),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        envelope.Should().NotBeNull();

        // Perform durable fan-out
        await using (var scope = _serviceProvider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
            var strategy = db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await db.Database.BeginTransactionAsync();

                var alreadyProcessed = await db.WebhookEventFanouts
                    .AnyAsync(f => f.EventId == envelope!.EventId);

                if (!alreadyProcessed)
                {
                    var matchingEndpoints = await db.WebhookEndpoints
                        .Where(e => e.TenantId == envelope!.TenantId && e.IsActive)
                        .Where(e => e.Subscriptions.Any(s => s.EventType == envelope.EventType))
                        .ToListAsync();

                    foreach (var ep in matchingEndpoints)
                    {
                        var delivery = new WebhookDelivery(
                            Guid.NewGuid(),
                            envelope.EventId,
                            ep.Id,
                            envelope.TenantId,
                            DateTimeOffset.UtcNow);

                        db.WebhookDeliveries.Add(delivery);
                    }

                    var fanout = new WebhookEventFanout(
                        Guid.NewGuid(),
                        envelope.EventId,
                        envelope.TenantId,
                        DateTimeOffset.UtcNow,
                        matchingEndpoints.Count);

                    db.WebhookEventFanouts.Add(fanout);
                    await db.SaveChangesAsync();
                }

                await tx.CommitAsync();
            });
        }

        await receiver.CompleteMessageAsync(sbMessage);

        // Verify WebhookDelivery row exists
        var pendingDelivery = await _dbContext.WebhookDeliveries
            .FirstOrDefaultAsync(d => d.EventId == eventId && d.EndpointId == createdEp.Id);
        pendingDelivery.Should().NotBeNull();
        pendingDelivery!.Status.Should().Be(WebhookDeliveryStatus.Pending);

        // Step 6: Dispatch Webhook Delivery
        var signer = _serviceProvider.GetRequiredService<IWebhookSigner>();
        var deliveryDispatcher = _serviceProvider.GetRequiredService<WebhookDeliveryDispatcher>();
        var deliveredCount = await deliveryDispatcher.ProcessDeliveriesAsync();
        deliveredCount.Should().Be(1);

        // Assert: Database status is Delivered with HTTP 200
        var deliveredRecord = await _dbContext.WebhookDeliveries
            .AsNoTracking()
            .FirstAsync(d => d.Id == pendingDelivery.Id);
        deliveredRecord.Status.Should().Be(WebhookDeliveryStatus.Delivered);
        deliveredRecord.LastHttpStatusCode.Should().Be(200);
        deliveredRecord.AttemptCount.Should().Be(1);

        // Assert: HTTP Receiver received the request with correct headers and HMAC
        receivedEventId.Should().Be(eventId.ToString("D"));
        receivedDeliveryId.Should().Be(pendingDelivery.Id.ToString("D"));
        receivedSignature.Should().NotBeNullOrEmpty();
        receivedBody.Should().NotBeNull();

        var isValidSignature = signer.Verify(rawSigningSecret, receivedBody!, receivedSignature!, TimeSpan.FromMinutes(5));
        isValidSignature.Should().BeTrue("HMAC signature must verify against one-time returned secret");

        // Step 7: Verify Delivery is queryable via GET /v1/deliveries/{id}
        var deliveryQueryResponse = await _apiClient.GetAsync($"/v1/deliveries/{pendingDelivery.Id}");
        deliveryQueryResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var deliveryDto = await deliveryQueryResponse.Content.ReadFromJsonAsync<WebhookDeliveryDto>();
        deliveryDto.Should().NotBeNull();
        deliveryDto!.Status.Should().Be("Delivered");
        deliveryDto.LastHttpStatusCode.Should().Be(200);
        deliveryDto.Attempts.Should().HaveCount(1);
        deliveryDto.Attempts[0].Outcome.Should().Be("Succeeded");
    }

    private static async Task<TestReceiverHost> StartTestReceiverAsync(RequestDelegate handler)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(opts => opts.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.Run(handler);
        await app.StartAsync();
        return new TestReceiverHost(app);
    }

    private sealed class TestReceiverHost : IDisposable
    {
        private readonly WebApplication _app;

        public TestReceiverHost(WebApplication app)
        {
            _app = app;
        }

        public string GetListeningUrl() => _app.Urls.First();

        public void Dispose()
        {
            _app.StopAsync().GetAwaiter().GetResult();
            _app.DisposeAsync().GetAwaiter().GetResult();
        }
    }
}
