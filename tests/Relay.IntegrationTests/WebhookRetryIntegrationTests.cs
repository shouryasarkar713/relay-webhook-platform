using System.Net;
using System.Net.Http.Json;
using System.Text;
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
using Relay.Application.Options;
using Relay.Domain.Entities;
using Relay.Domain.Enums;
using Relay.Infrastructure;
using Relay.Infrastructure.Persistence;
using Relay.Infrastructure.Security;
using Relay.Infrastructure.Services;
using Relay.IntegrationTests.Fixtures;
using Relay.Worker.Options;
using Relay.Worker.Services;
using Xunit;
using Xunit.Abstractions;

namespace Relay.IntegrationTests;

[Collection("DatabaseCollection")]
public sealed class WebhookRetryIntegrationTests : IAsyncLifetime
{
    private readonly RelayApiFactory _factory;
    private readonly ITestOutputHelper _output;
    private readonly ServiceProvider _serviceProvider;
    private readonly RelayDbContext _dbContext;
    private readonly HttpClient _apiClient;

    public WebhookRetryIntegrationTests(RelayApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
        _apiClient = _factory.CreateClient();

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

        services.AddScoped<IWebhookDeliveryStore, WebhookDeliveryStore>();
        services.AddSingleton<IWebhookSigner, WebhookSigner>();
        services.AddSingleton<IEndpointSecretProtector, DataProtectionEndpointSecretProtector>();
        services.AddDataProtection().SetApplicationName("Relay.Tests");

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
            opt.MaxAttempts = 3;
            opt.BaseDelaySeconds = 1.0;
            opt.MaxDelaySeconds = 10.0;
            opt.MaxRetryWindowHours = 24;
            opt.MaxServerRequestedDelaySeconds = 300.0;
        });

        services.Configure<CircuitBreakerOptions>(opt =>
        {
            opt.FailureThreshold = 3;
            opt.OpenDurationSeconds = 10;
            opt.HalfOpenProbeLimit = 1;
        });

        services.Configure<BulkheadOptions>(opt =>
        {
            opt.MaxGlobalConcurrency = 50;
            opt.MaxPerEndpointConcurrency = 5;
        });

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

        services.AddScoped<WebhookDeliveryDispatcher>();

        _serviceProvider = services.BuildServiceProvider();
        _dbContext = _serviceProvider.GetRequiredService<RelayDbContext>();
    }

    public async Task InitializeAsync()
    {
        await _dbContext.WebhookDeliveryReplayAudits.ExecuteDeleteAsync();
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
    public async Task ScenarioA_TemporaryFailure_RetriesAndEventuallyDelivers()
    {
        // Receiver fails 500, then 503, then succeeds 200
        var attemptCounter = 0;
        using var receiverHost = await StartTestReceiverAsync(context =>
        {
            attemptCounter++;
            if (attemptCounter == 1)
            {
                context.Response.StatusCode = 500;
                return context.Response.WriteAsync("Internal Server Error");
            }
            if (attemptCounter == 2)
            {
                context.Response.StatusCode = 503;
                return context.Response.WriteAsync("Service Unavailable");
            }

            context.Response.StatusCode = 200;
            return context.Response.WriteAsync("""{"delivered":true}""");
        });

        var tenantId = Guid.Parse(RelayApiFactory.TenantAId);
        var now = DateTimeOffset.UtcNow;
        var eventId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        var endpointId = Guid.NewGuid();

        var protector = _serviceProvider.GetRequiredService<IEndpointSecretProtector>();
        var endpoint = new WebhookEndpoint(endpointId, tenantId, "Retry EP", receiverHost.GetListeningUrl(), protector.Protect("whsec_scen_a"), now);
        var evt = new Event(eventId, tenantId, "payment.failed", "{}", "hash_a", "idem_scen_a", now);
        var delivery = new WebhookDelivery(deliveryId, eventId, endpointId, tenantId, now);

        _dbContext.WebhookEndpoints.Add(endpoint);
        _dbContext.Events.Add(evt);
        _dbContext.WebhookDeliveries.Add(delivery);
        await _dbContext.SaveChangesAsync();

        var dispatcher = _serviceProvider.GetRequiredService<WebhookDeliveryDispatcher>();

        // 1st Dispatch: returns 500 -> Pending with NextAttemptAtUtc in future
        var processed1 = await dispatcher.ProcessDeliveriesAsync();
        processed1.Should().Be(1);

        var d1 = await _dbContext.WebhookDeliveries.AsNoTracking().FirstAsync(d => d.Id == deliveryId);
        d1.Status.Should().Be(WebhookDeliveryStatus.Pending);
        d1.AttemptCount.Should().Be(1);
        d1.LastHttpStatusCode.Should().Be(500);
        d1.NextAttemptAtUtc.Should().BeAfter(now);

        // Simulate backoff delay elapsing by resetting NextAttemptAtUtc to past
        await _dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE dbo.WebhookDeliveries SET NextAttemptAtUtc = DATEADD(minute, -1, GETUTCDATE()) WHERE Id = {0}",
            deliveryId);

        // 2nd Dispatch: returns 503 -> Pending with NextAttemptAtUtc in future
        var processed2 = await dispatcher.ProcessDeliveriesAsync();
        processed2.Should().Be(1);

        var d2 = await _dbContext.WebhookDeliveries.AsNoTracking().FirstAsync(d => d.Id == deliveryId);
        d2.Status.Should().Be(WebhookDeliveryStatus.Pending);
        d2.AttemptCount.Should().Be(2);
        d2.LastHttpStatusCode.Should().Be(503);

        // Simulate backoff delay elapsing again
        await _dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE dbo.WebhookDeliveries SET NextAttemptAtUtc = DATEADD(minute, -1, GETUTCDATE()) WHERE Id = {0}",
            deliveryId);

        // 3rd Dispatch: returns 200 -> Delivered
        var processed3 = await dispatcher.ProcessDeliveriesAsync();
        processed3.Should().Be(1);

        var d3 = await _dbContext.WebhookDeliveries.AsNoTracking().FirstAsync(d => d.Id == deliveryId);
        d3.Status.Should().Be(WebhookDeliveryStatus.Delivered);
        d3.AttemptCount.Should().Be(3);
        d3.LastHttpStatusCode.Should().Be(200);
        d3.DeliveredAtUtc.Should().NotBeNull();

        // Verify attempt history has all 3 records preserved
        var attempts = await _dbContext.WebhookDeliveryAttempts
            .AsNoTracking()
            .Where(a => a.DeliveryId == deliveryId)
            .OrderBy(a => a.AttemptNumber)
            .ToListAsync();

        attempts.Should().HaveCount(3);
        attempts[0].AttemptNumber.Should().Be(1);
        attempts[0].Outcome.Should().Be(DeliveryAttemptOutcome.RetryableHttpFailure);
        attempts[0].HttpStatusCode.Should().Be(500);

        attempts[1].AttemptNumber.Should().Be(2);
        attempts[1].Outcome.Should().Be(DeliveryAttemptOutcome.RetryableHttpFailure);
        attempts[1].HttpStatusCode.Should().Be(503);

        attempts[2].AttemptNumber.Should().Be(3);
        attempts[2].Outcome.Should().Be(DeliveryAttemptOutcome.Succeeded);
        attempts[2].HttpStatusCode.Should().Be(200);
    }

    [Fact]
    public async Task ScenarioB_RateLimitingWithRetryAfter_SchedulesAccordingToHeader()
    {
        // Receiver returns 429 with Retry-After: 5
        using var receiverHost = await StartTestReceiverAsync(context =>
        {
            context.Response.StatusCode = 429;
            context.Response.Headers.Append("Retry-After", "5");
            return context.Response.WriteAsync("Rate Limited");
        });

        var tenantId = Guid.Parse(RelayApiFactory.TenantAId);
        var now = DateTimeOffset.UtcNow;
        var eventId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        var endpointId = Guid.NewGuid();

        var protector = _serviceProvider.GetRequiredService<IEndpointSecretProtector>();
        var endpoint = new WebhookEndpoint(endpointId, tenantId, "429 EP", receiverHost.GetListeningUrl(), protector.Protect("whsec_429"), now);
        var evt = new Event(eventId, tenantId, "order.created", "{}", "hash_429", "idem_429", now);
        var delivery = new WebhookDelivery(deliveryId, eventId, endpointId, tenantId, now);

        _dbContext.WebhookEndpoints.Add(endpoint);
        _dbContext.Events.Add(evt);
        _dbContext.WebhookDeliveries.Add(delivery);
        await _dbContext.SaveChangesAsync();

        var dispatcher = _serviceProvider.GetRequiredService<WebhookDeliveryDispatcher>();
        var beforeDispatch = DateTimeOffset.UtcNow;
        await dispatcher.ProcessDeliveriesAsync();

        var d = await _dbContext.WebhookDeliveries.AsNoTracking().FirstAsync(d => d.Id == deliveryId);
        d.Status.Should().Be(WebhookDeliveryStatus.Pending);
        d.AttemptCount.Should().Be(1);
        d.LastHttpStatusCode.Should().Be(429);
        // NextAttemptAtUtc must be at least beforeDispatch + 5s (never earlier than server requested)
        d.NextAttemptAtUtc.Should().BeOnOrAfter(beforeDispatch.AddSeconds(4.5));
    }

    [Fact]
    public async Task ScenarioC_RetryExhaustion_TransitionsToDeadLettered()
    {
        // Receiver always returns 500
        using var receiverHost = await StartTestReceiverAsync(context =>
        {
            context.Response.StatusCode = 500;
            return context.Response.WriteAsync("Persistent Server Error");
        });

        var tenantId = Guid.Parse(RelayApiFactory.TenantAId);
        var now = DateTimeOffset.UtcNow;
        var eventId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        var endpointId = Guid.NewGuid();

        var protector = _serviceProvider.GetRequiredService<IEndpointSecretProtector>();
        var endpoint = new WebhookEndpoint(endpointId, tenantId, "Exhaust EP", receiverHost.GetListeningUrl(), protector.Protect("whsec_ex"), now);
        var evt = new Event(eventId, tenantId, "user.created", "{}", "hash_ex", "idem_ex", now);
        var delivery = new WebhookDelivery(deliveryId, eventId, endpointId, tenantId, now);

        _dbContext.WebhookEndpoints.Add(endpoint);
        _dbContext.Events.Add(evt);
        _dbContext.WebhookDeliveries.Add(delivery);
        await _dbContext.SaveChangesAsync();

        var dispatcher = _serviceProvider.GetRequiredService<WebhookDeliveryDispatcher>();

        // Attempt 1 -> Pending
        await dispatcher.ProcessDeliveriesAsync();
        var d1 = await _dbContext.WebhookDeliveries.AsNoTracking().FirstAsync(d => d.Id == deliveryId);
        d1.Status.Should().Be(WebhookDeliveryStatus.Pending);

        await _dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE dbo.WebhookDeliveries SET NextAttemptAtUtc = DATEADD(minute, -1, GETUTCDATE()) WHERE Id = {0}",
            deliveryId);

        // Attempt 2 -> Pending
        await dispatcher.ProcessDeliveriesAsync();
        var d2 = await _dbContext.WebhookDeliveries.AsNoTracking().FirstAsync(d => d.Id == deliveryId);
        d2.Status.Should().Be(WebhookDeliveryStatus.Pending);

        await _dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE dbo.WebhookDeliveries SET NextAttemptAtUtc = DATEADD(minute, -1, GETUTCDATE()) WHERE Id = {0}",
            deliveryId);

        // Attempt 3 (MaxAttempts = 3) -> DeadLettered!
        await dispatcher.ProcessDeliveriesAsync();
        var d3 = await _dbContext.WebhookDeliveries.AsNoTracking().FirstAsync(d => d.Id == deliveryId);
        d3.Status.Should().Be(WebhookDeliveryStatus.DeadLettered);
        d3.AttemptCount.Should().Be(3);
        d3.DeadLetteredAtUtc.Should().NotBeNull();
        d3.LastError.Should().Contain("Retry budget exhausted");

        // 4th dispatch pass should NOT pick up DeadLettered message
        var processed4 = await dispatcher.ProcessDeliveriesAsync();
        processed4.Should().Be(0);

        var d4 = await _dbContext.WebhookDeliveries.AsNoTracking().FirstAsync(d => d.Id == deliveryId);
        d4.AttemptCount.Should().Be(3, "Dead-lettered messages must never be picked up for delivery attempts automatically");
    }

    [Fact]
    public async Task ScenarioD_ManualReplay_ResetsPending_PreservesHistory_AndDelivers()
    {
        // 1. Arrange a DeadLettered delivery
        var tenantId = Guid.Parse(RelayApiFactory.TenantAId);
        var now = DateTimeOffset.UtcNow;
        var eventId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        var endpointId = Guid.NewGuid();

        var returnSuccess = false;
        using var receiverHost = await StartTestReceiverAsync(context =>
        {
            if (!returnSuccess)
            {
                context.Response.StatusCode = 500;
                return context.Response.WriteAsync("Error");
            }
            context.Response.StatusCode = 200;
            return context.Response.WriteAsync("""{"replayed":true}""");
        });

        var protector = _serviceProvider.GetRequiredService<IEndpointSecretProtector>();
        var endpoint = new WebhookEndpoint(endpointId, tenantId, "Replay EP", receiverHost.GetListeningUrl(), protector.Protect("whsec_rep"), now);
        var evt = new Event(eventId, tenantId, "invoice.overdue", "{}", "hash_rep", "idem_rep", now);
        var delivery = new WebhookDelivery(deliveryId, eventId, endpointId, tenantId, now, WebhookDeliveryStatus.DeadLettered);

        _dbContext.WebhookEndpoints.Add(endpoint);
        _dbContext.Events.Add(evt);
        _dbContext.WebhookDeliveries.Add(delivery);

        // Prepopulate 3 attempts
        for (int i = 1; i <= 3; i++)
        {
            _dbContext.WebhookDeliveryAttempts.Add(new WebhookDeliveryAttempt(
                Guid.NewGuid(), deliveryId, i, now.AddMinutes(-10 + i), now.AddMinutes(-10 + i), 100, 500,
                DeliveryAttemptOutcome.RetryableHttpFailure, "HttpStatus500", "Failed"));
        }
        await _dbContext.SaveChangesAsync();

        await _dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE dbo.WebhookDeliveries SET AttemptCount = 3 WHERE Id = {0}",
            deliveryId);

        // 2. Act: Call POST /v1/deliveries/{deliveryId}/replay via API
        var replayKey = $"replay-{Guid.NewGuid():N}";
        using var replayReq = new HttpRequestMessage(HttpMethod.Post, $"/v1/deliveries/{deliveryId}/replay");
        replayReq.Headers.Add("X-API-Key", RelayApiFactory.ApiKeyA);
        replayReq.Headers.Add("Idempotency-Key", replayKey);

        var replayResp = await _apiClient.SendAsync(replayReq);
        replayResp.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var replayBody = await replayResp.Content.ReadFromJsonAsync<ReplayDeliveryResponse>();
        replayBody.Should().NotBeNull();
        replayBody!.Status.Should().Be("Pending");
        replayBody.ReplayCount.Should().Be(1);

        // 3. Verify Database state: Status is Pending, ReplayCount is 1, DeadLetteredAtUtc is null
        var afterReplayDb = await _dbContext.WebhookDeliveries.AsNoTracking().FirstAsync(d => d.Id == deliveryId);
        afterReplayDb.Status.Should().Be(WebhookDeliveryStatus.Pending);
        afterReplayDb.ReplayCount.Should().Be(1);
        afterReplayDb.DeadLetteredAtUtc.Should().BeNull();

        // Verify Replay Audit record created
        var audit = await _dbContext.WebhookDeliveryReplayAudits.FirstOrDefaultAsync(a => a.DeliveryId == deliveryId);
        audit.Should().NotBeNull();
        audit!.ReplayKey.Should().Be(replayKey);
        audit.TenantId.Should().Be(tenantId);

        // 4. Duplicate Replay call with same Idempotency-Key is idempotent
        using var dupReplayReq = new HttpRequestMessage(HttpMethod.Post, $"/v1/deliveries/{deliveryId}/replay");
        dupReplayReq.Headers.Add("X-API-Key", RelayApiFactory.ApiKeyA);
        dupReplayReq.Headers.Add("Idempotency-Key", replayKey);

        var dupReplayResp = await _apiClient.SendAsync(dupReplayReq);
        dupReplayResp.StatusCode.Should().Be(HttpStatusCode.Accepted);

        // 5. Run Dispatcher with receiver returning 200
        returnSuccess = true;
        var dispatcher = _serviceProvider.GetRequiredService<WebhookDeliveryDispatcher>();
        var processed = await dispatcher.ProcessDeliveriesAsync();
        processed.Should().Be(1);

        // 6. Assert Delivered with 4 total attempts
        var deliveredRecord = await _dbContext.WebhookDeliveries.AsNoTracking().FirstAsync(d => d.Id == deliveryId);
        deliveredRecord.Status.Should().Be(WebhookDeliveryStatus.Delivered);
        deliveredRecord.AttemptCount.Should().Be(4);
        deliveredRecord.DeliveredAtUtc.Should().NotBeNull();

        var attempts = await _dbContext.WebhookDeliveryAttempts
            .AsNoTracking()
            .Where(a => a.DeliveryId == deliveryId)
            .OrderBy(a => a.AttemptNumber)
            .ToListAsync();
        attempts.Should().HaveCount(4);
        attempts[3].Outcome.Should().Be(DeliveryAttemptOutcome.Succeeded);
        attempts[3].AttemptNumber.Should().Be(4);
    }

    [Fact]
    public async Task ScenarioE_CircuitBreaker_TripsAfterThresholdFailures_DefersWithoutConsumingBudget()
    {
        // Receiver always returns 500
        using var receiverHost = await StartTestReceiverAsync(context =>
        {
            context.Response.StatusCode = 500;
            return context.Response.WriteAsync("Crash");
        });

        var tenantId = Guid.Parse(RelayApiFactory.TenantAId);
        var now = DateTimeOffset.UtcNow;
        var endpointId = Guid.NewGuid();

        var protector = _serviceProvider.GetRequiredService<IEndpointSecretProtector>();
        var endpoint = new WebhookEndpoint(endpointId, tenantId, "Circuit EP", receiverHost.GetListeningUrl(), protector.Protect("whsec_cb"), now);
        _dbContext.WebhookEndpoints.Add(endpoint);

        // Create 3 deliveries that will fail and trip the circuit (threshold = 3)
        var d1 = new WebhookDelivery(Guid.NewGuid(), Guid.NewGuid(), endpointId, tenantId, now);
        var d2 = new WebhookDelivery(Guid.NewGuid(), Guid.NewGuid(), endpointId, tenantId, now);
        var d3 = new WebhookDelivery(Guid.NewGuid(), Guid.NewGuid(), endpointId, tenantId, now);
        var d4 = new WebhookDelivery(Guid.NewGuid(), Guid.NewGuid(), endpointId, tenantId, now); // Should be deferred!

        _dbContext.Events.Add(new Event(d1.EventId, tenantId, "evt.1", "{}", "h1", "id1", now));
        _dbContext.Events.Add(new Event(d2.EventId, tenantId, "evt.2", "{}", "h2", "id2", now));
        _dbContext.Events.Add(new Event(d3.EventId, tenantId, "evt.3", "{}", "h3", "id3", now));
        _dbContext.Events.Add(new Event(d4.EventId, tenantId, "evt.4", "{}", "h4", "id4", now));

        _dbContext.WebhookDeliveries.AddRange(d1, d2, d3);
        await _dbContext.SaveChangesAsync();

        var dispatcher = _serviceProvider.GetRequiredService<WebhookDeliveryDispatcher>();

        // Process first 3 deliveries -> consecutive failure count reaches 3 -> circuit trips to Open
        await dispatcher.ProcessDeliveriesAsync();

        var epAfter3 = await _dbContext.WebhookEndpoints.AsNoTracking().FirstAsync(e => e.Id == endpointId);
        epAfter3.CircuitState.Should().Be(EndpointCircuitState.Open);
        epAfter3.ConsecutiveFailureCount.Should().Be(3);

        // Now add the 4th delivery
        _dbContext.WebhookDeliveries.Add(d4);
        await _dbContext.SaveChangesAsync();

        // Process 4th delivery -> circuit is Open -> deferred without initiating HTTP attempt!
        await dispatcher.ProcessDeliveriesAsync();

        var d4After = await _dbContext.WebhookDeliveries.AsNoTracking().FirstAsync(d => d.Id == d4.Id);
        d4After.Status.Should().Be(WebhookDeliveryStatus.Pending, "Deferred delivery remains Pending");
        d4After.AttemptCount.Should().Be(0, "Deferred delivery must NOT consume attempt budget");
        d4After.LastErrorCategory.Should().Be("CircuitBreakerDeferred");
        d4After.NextAttemptAtUtc.Should().BeAfter(now);

        var d4Attempts = await _dbContext.WebhookDeliveryAttempts.AsNoTracking().Where(a => a.DeliveryId == d4.Id).ToListAsync();
        d4Attempts.Should().BeEmpty("No delivery attempt record should be inserted for circuit-deferred delivery");
    }

    [Fact]
    public async Task ClaimPendingDeliveries_IncrementsClaimCount_WithoutIncrementingAttemptCount()
    {
        var tenantId = Guid.Parse(RelayApiFactory.TenantAId);
        var now = DateTimeOffset.UtcNow;
        var deliveryId = Guid.NewGuid();
        var endpointId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        var protector = _serviceProvider.GetRequiredService<IEndpointSecretProtector>();
        var endpoint = new WebhookEndpoint(endpointId, tenantId, "Claim EP", "http://localhost:59998/ep", protector.Protect("whsec_cl"), now);
        var evt = new Event(eventId, tenantId, "evt.claim", "{}", "hc", "ic", now);
        var delivery = new WebhookDelivery(deliveryId, eventId, endpointId, tenantId, now);

        _dbContext.WebhookEndpoints.Add(endpoint);
        _dbContext.Events.Add(evt);
        _dbContext.WebhookDeliveries.Add(delivery);
        await _dbContext.SaveChangesAsync();

        var store = _serviceProvider.GetRequiredService<IWebhookDeliveryStore>();

        // Act: Claim the delivery record
        var leaseToken = Guid.NewGuid();
        var claimed = await store.ClaimPendingDeliveriesAsync(10, leaseToken, TimeSpan.FromMinutes(1));

        claimed.Should().Contain(d => d.Id == deliveryId);

        var dbDelivery = await _dbContext.WebhookDeliveries.AsNoTracking().FirstAsync(d => d.Id == deliveryId);
        dbDelivery.Status.Should().Be(WebhookDeliveryStatus.Processing);
        dbDelivery.ClaimCount.Should().Be(1, "Claiming must increment ClaimCount");
        dbDelivery.AttemptCount.Should().Be(0, "Claiming must NOT increment AttemptCount; only actual HTTP initiation increments AttemptCount");
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

        public TestReceiverHost(WebApplication app) => _app = app;

        public string GetListeningUrl() => _app.Urls.First();

        public void Dispose()
        {
            _app.StopAsync().GetAwaiter().GetResult();
            _app.DisposeAsync().GetAwaiter().GetResult();
        }
    }
}
