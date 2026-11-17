using System.Net;
using System.Text;
using System.Text.Json;
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
public sealed class WebhookDeliveryIntegrationTests : IAsyncLifetime
{
    private readonly RelayApiFactory _factory;
    private readonly ITestOutputHelper _output;
    private readonly ServiceProvider _serviceProvider;
    private readonly RelayDbContext _dbContext;

    public WebhookDeliveryIntegrationTests(RelayApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;

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
            opt.MaxConcurrency = 2;
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
    public async Task DeliveryDispatcher_Success200_MarksDeliveredAndSignsHmac()
    {
        // 1. Start local mock HTTP receiver server
        string? receivedSignatureHeader = null;
        string? receivedEventIdHeader = null;
        string? receivedDeliveryIdHeader = null;
        byte[]? receivedBodyBytes = null;

        using var receiverHost = await StartTestReceiverAsync(async context =>
        {
            receivedSignatureHeader = context.Request.Headers["X-Relay-Signature"];
            receivedEventIdHeader = context.Request.Headers["X-Relay-Event-Id"];
            receivedDeliveryIdHeader = context.Request.Headers["X-Relay-Delivery-Id"];

            using var ms = new MemoryStream();
            await context.Request.Body.CopyToAsync(ms);
            receivedBodyBytes = ms.ToArray();

            context.Response.StatusCode = 200;
            await context.Response.WriteAsync("""{"received":true}""");
        });

        var receiverUrl = receiverHost.GetListeningUrl();

        // 2. Arrange DB entities
        var tenantId = Guid.Parse(RelayApiFactory.TenantAId);
        var now = DateTimeOffset.UtcNow;
        var eventId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        var endpointId = Guid.NewGuid();

        var protector = _serviceProvider.GetRequiredService<IEndpointSecretProtector>();
        var rawSecret = "whsec_0123456789abcdef0123456789abcdef";
        var protectedSecret = protector.Protect(rawSecret);

        var endpoint = new WebhookEndpoint(endpointId, tenantId, "Test EP", receiverUrl, protectedSecret, now);
        var evt = new Event(eventId, tenantId, "invoice.paid", """{"invoiceId":"INV-101","amount":500}""", "hash101", "idem_deliv_1", now);
        var delivery = new WebhookDelivery(deliveryId, eventId, endpointId, tenantId, now);

        _dbContext.WebhookEndpoints.Add(endpoint);
        _dbContext.Events.Add(evt);
        _dbContext.WebhookDeliveries.Add(delivery);
        await _dbContext.SaveChangesAsync();

        // 3. Act: Run dispatcher
        var dispatcher = _serviceProvider.GetRequiredService<WebhookDeliveryDispatcher>();
        var processed = await dispatcher.ProcessDeliveriesAsync();

        // 4. Assert: Delivery is Delivered
        processed.Should().Be(1);
        var dbDelivery = await _dbContext.WebhookDeliveries.AsNoTracking().FirstAsync(d => d.Id == deliveryId);
        dbDelivery.Status.Should().Be(WebhookDeliveryStatus.Delivered);
        dbDelivery.DeliveredAtUtc.Should().NotBeNull();
        dbDelivery.LastHttpStatusCode.Should().Be(200);
        dbDelivery.AttemptCount.Should().Be(1);

        var attempts = await _dbContext.WebhookDeliveryAttempts.AsNoTracking().Where(a => a.DeliveryId == deliveryId).ToListAsync();
        attempts.Should().HaveCount(1);
        attempts[0].Outcome.Should().Be(DeliveryAttemptOutcome.Succeeded);
        attempts[0].HttpStatusCode.Should().Be(200);
        attempts[0].ResponseExcerpt.Should().Contain("received");

        // Assert Headers & HMAC Signature
        receivedEventIdHeader.Should().Be(eventId.ToString("D"));
        receivedDeliveryIdHeader.Should().Be(deliveryId.ToString("D"));
        receivedSignatureHeader.Should().NotBeNullOrEmpty();

        var signer = _serviceProvider.GetRequiredService<IWebhookSigner>();
        var isValidHmac = signer.Verify(rawSecret, receivedBodyBytes!, receivedSignatureHeader!, TimeSpan.FromMinutes(5));
        isValidHmac.Should().BeTrue("The HTTP receiver must be able to independently verify the HMAC-SHA256 signature");
    }

    [Fact]
    public async Task DeliveryDispatcher_Http500_SchedulesRetryWithPendingStatus()
    {
        using var receiverHost = await StartTestReceiverAsync(context =>
        {
            context.Response.StatusCode = 500;
            return context.Response.WriteAsync("Internal Server Error");
        });

        var receiverUrl = receiverHost.GetListeningUrl();
        var tenantId = Guid.Parse(RelayApiFactory.TenantAId);
        var now = DateTimeOffset.UtcNow;
        var eventId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        var endpointId = Guid.NewGuid();

        var protector = _serviceProvider.GetRequiredService<IEndpointSecretProtector>();
        var endpoint = new WebhookEndpoint(endpointId, tenantId, "500 EP", receiverUrl, protector.Protect("whsec_500"), now);
        var evt = new Event(eventId, tenantId, "order.failed", "{}", "hash_f", "idem_500", now);
        var delivery = new WebhookDelivery(deliveryId, eventId, endpointId, tenantId, now);

        _dbContext.WebhookEndpoints.Add(endpoint);
        _dbContext.Events.Add(evt);
        _dbContext.WebhookDeliveries.Add(delivery);
        await _dbContext.SaveChangesAsync();

        var dispatcher = _serviceProvider.GetRequiredService<WebhookDeliveryDispatcher>();
        await dispatcher.ProcessDeliveriesAsync();

        var dbDelivery = await _dbContext.WebhookDeliveries.AsNoTracking().FirstAsync(d => d.Id == deliveryId);
        dbDelivery.Status.Should().Be(WebhookDeliveryStatus.Pending, "Retryable failure must remain Pending for subsequent retry attempt");
        dbDelivery.LastHttpStatusCode.Should().Be(500);
        dbDelivery.AttemptCount.Should().Be(1);
        dbDelivery.NextAttemptAtUtc.Should().BeAfter(now);

        var attempts = await _dbContext.WebhookDeliveryAttempts.AsNoTracking().Where(a => a.DeliveryId == deliveryId).ToListAsync();
        attempts.Should().HaveCount(1);
        attempts[0].Outcome.Should().Be(DeliveryAttemptOutcome.RetryableHttpFailure);
        attempts[0].HttpStatusCode.Should().Be(500);
    }

    [Fact]
    public async Task DeliveryDispatcher_Http400_MarksFailedPermanently()
    {
        using var receiverHost = await StartTestReceiverAsync(context =>
        {
            context.Response.StatusCode = 400;
            return context.Response.WriteAsync("Bad Request");
        });

        var receiverUrl = receiverHost.GetListeningUrl();
        var tenantId = Guid.Parse(RelayApiFactory.TenantAId);
        var now = DateTimeOffset.UtcNow;
        var eventId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        var endpointId = Guid.NewGuid();

        var protector = _serviceProvider.GetRequiredService<IEndpointSecretProtector>();
        var endpoint = new WebhookEndpoint(endpointId, tenantId, "400 EP", receiverUrl, protector.Protect("whsec_400"), now);
        var evt = new Event(eventId, tenantId, "order.bad", "{}", "hash_b", "idem_400", now);
        var delivery = new WebhookDelivery(deliveryId, eventId, endpointId, tenantId, now);

        _dbContext.WebhookEndpoints.Add(endpoint);
        _dbContext.Events.Add(evt);
        _dbContext.WebhookDeliveries.Add(delivery);
        await _dbContext.SaveChangesAsync();

        var dispatcher = _serviceProvider.GetRequiredService<WebhookDeliveryDispatcher>();
        await dispatcher.ProcessDeliveriesAsync();

        var dbDelivery = await _dbContext.WebhookDeliveries.AsNoTracking().FirstAsync(d => d.Id == deliveryId);
        dbDelivery.Status.Should().Be(WebhookDeliveryStatus.Failed, "Client error 400 is permanent and must mark delivery Failed");
        dbDelivery.LastHttpStatusCode.Should().Be(400);
        dbDelivery.AttemptCount.Should().Be(1);

        var attempts = await _dbContext.WebhookDeliveryAttempts.AsNoTracking().Where(a => a.DeliveryId == deliveryId).ToListAsync();
        attempts.Should().HaveCount(1);
        attempts[0].Outcome.Should().Be(DeliveryAttemptOutcome.PermanentHttpFailure);
        attempts[0].HttpStatusCode.Should().Be(400);
    }

    [Fact]
    public async Task DeliveryDispatcher_InactiveEndpoint_MarksSkipped()
    {
        var tenantId = Guid.Parse(RelayApiFactory.TenantAId);
        var now = DateTimeOffset.UtcNow;
        var eventId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        var endpointId = Guid.NewGuid();

        var protector = _serviceProvider.GetRequiredService<IEndpointSecretProtector>();
        var endpoint = new WebhookEndpoint(endpointId, tenantId, "Inactive EP", "http://localhost:59999/dummy", protector.Protect("whsec_inact"), now, isActive: false);
        var evt = new Event(eventId, tenantId, "order.skipped", "{}", "hash_s", "idem_skip", now);
        var delivery = new WebhookDelivery(deliveryId, eventId, endpointId, tenantId, now);

        _dbContext.WebhookEndpoints.Add(endpoint);
        _dbContext.Events.Add(evt);
        _dbContext.WebhookDeliveries.Add(delivery);
        await _dbContext.SaveChangesAsync();

        var dispatcher = _serviceProvider.GetRequiredService<WebhookDeliveryDispatcher>();
        await dispatcher.ProcessDeliveriesAsync();

        var dbDelivery = await _dbContext.WebhookDeliveries.AsNoTracking().FirstAsync(d => d.Id == deliveryId);
        dbDelivery.Status.Should().Be(WebhookDeliveryStatus.Skipped);
        dbDelivery.LastError.Should().Contain("inactive");
    }

    private static async Task<TestReceiverHost> StartTestReceiverAsync(RequestDelegate handler)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(opts => opts.Listen(IPAddress.Loopback, 0)); // Dynamic port
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
