using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
using Xunit;
using Xunit.Abstractions;

namespace Relay.IntegrationTests;

[Collection("DatabaseCollection")]
public sealed class MultiWorkerReliabilityIntegrationTests : IAsyncLifetime
{
    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;
        public MutableTimeProvider(DateTimeOffset initial) => _utcNow = initial;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
        public void SetUtcNow(DateTimeOffset time) => _utcNow = time;
    }

    private readonly RelayApiFactory _factory;
    private readonly ITestOutputHelper _output;
    private readonly MutableTimeProvider _timeProvider;
    private readonly ServiceProvider _serviceProvider;
    private readonly RelayDbContext _dbContext;

    public MultiWorkerReliabilityIntegrationTests(RelayApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
        _timeProvider = new MutableTimeProvider(DateTimeOffset.UtcNow);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(_timeProvider);
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
            opt.MaxConcurrency = 4;
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
            opt.FailureThreshold = 3;
            opt.OpenDurationSeconds = 60;
            opt.ProbeLeaseDurationSeconds = 30;
        });

        _serviceProvider = services.BuildServiceProvider();
        _dbContext = _serviceProvider.GetRequiredService<RelayDbContext>();
    }

    public async Task InitializeAsync()
    {
        await _dbContext.WebhookDeliveryAttempts.ExecuteDeleteAsync();
        await _dbContext.WebhookDeliveryReplayAudits.ExecuteDeleteAsync();
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
    public async Task MultiWorkerClaiming_UnderHighConcurrency_DoesNotDuplicateClaims()
    {
        var tenantId = Guid.Parse(RelayApiFactory.TenantAId);
        var now = _timeProvider.GetUtcNow();

        var endpoint = new WebhookEndpoint(
            Guid.NewGuid(),
            tenantId,
            "MultiWorker Endpoint",
            "https://httpbin.org/post",
            "secret",
            now);

        _dbContext.WebhookEndpoints.Add(endpoint);

        var events = new List<Event>();
        var deliveries = new List<WebhookDelivery>();

        for (int i = 0; i < 10; i++)
        {
            var evt = new Event(
                Guid.NewGuid(),
                tenantId,
                $"order.created.{i}",
                $"{{\"orderId\":\"{i}\"}}",
                $"hash{i}",
                $"idem_multi_{i}",
                now);

            events.Add(evt);
            deliveries.Add(new WebhookDelivery(
                Guid.NewGuid(),
                evt.Id,
                endpoint.Id,
                tenantId,
                now));
        }

        _dbContext.Events.AddRange(events);
        _dbContext.WebhookDeliveries.AddRange(deliveries);
        await _dbContext.SaveChangesAsync();

        var worker1Lease = Guid.NewGuid();
        var worker2Lease = Guid.NewGuid();

        using var scope1 = _serviceProvider.CreateScope();
        using var scope2 = _serviceProvider.CreateScope();

        var store1 = scope1.ServiceProvider.GetRequiredService<IWebhookDeliveryStore>();
        var store2 = scope2.ServiceProvider.GetRequiredService<IWebhookDeliveryStore>();

        // Worker 1 claims first batch of 5
        var claimedByWorker1 = await store1.ClaimPendingDeliveriesAsync(5, worker1Lease, TimeSpan.FromMinutes(2));

        // Worker 2 claims second batch of 5
        var claimedByWorker2 = await store2.ClaimPendingDeliveriesAsync(5, worker2Lease, TimeSpan.FromMinutes(2));

        claimedByWorker1.Count.Should().Be(5);
        claimedByWorker2.Count.Should().Be(5);

        // Ensure disjoint sets (no double claiming)
        var ids1 = claimedByWorker1.Select(d => d.Id).ToHashSet();
        var ids2 = claimedByWorker2.Select(d => d.Id).ToHashSet();

        ids1.Intersect(ids2).Should().BeEmpty();
        (ids1.Count + ids2.Count).Should().Be(10);

        // Subsequent claim attempt returns 0
        var emptyClaim = await store2.ClaimPendingDeliveriesAsync(5, worker2Lease, TimeSpan.FromMinutes(2));
        emptyClaim.Should().BeEmpty();
    }

    [Fact]
    public async Task CircuitBreakerProbeCrash_RecoversAutomaticallyAfterLeaseExpires_AndFencesStaleWorker()
    {
        var tenantId = Guid.Parse(RelayApiFactory.TenantAId);
        var now = _timeProvider.GetUtcNow();

        var endpoint = new WebhookEndpoint(
            Guid.NewGuid(),
            tenantId,
            "Probe Recovery Endpoint",
            "https://httpbin.org/post",
            "secret",
            now);

        // Place in Open state with openedAt set to 65s ago (beyond 60s openDuration)
        endpoint.RecordDeliveryFailure(1, TimeSpan.FromSeconds(60), now.AddSeconds(-65));
        _dbContext.WebhookEndpoints.Add(endpoint);
        await _dbContext.SaveChangesAsync();

        using var scope = _serviceProvider.CreateScope();
        var cbManager = scope.ServiceProvider.GetRequiredService<IEndpointCircuitBreakerManager>();

        // Worker 1 acquires probe
        var decision1 = await cbManager.CanExecuteDecisionAsync(endpoint.Id);
        decision1.CanExecute.Should().BeTrue();
        decision1.IsProbe.Should().BeTrue();
        decision1.ProbeToken.Should().NotBeNull();

        // Worker 2 attempts concurrent probe: should be blocked
        var decision2 = await cbManager.CanExecuteDecisionAsync(endpoint.Id);
        decision2.CanExecute.Should().BeFalse();

        // Worker 1 "crashes" (never calls RecordSuccess or RecordFailure)
        // Advance time by 35 seconds (beyond the 30s probe lease)
        _timeProvider.Advance(TimeSpan.FromSeconds(35));

        // Worker 2 should now automatically reclaim the expired probe lease
        var decision3 = await cbManager.CanExecuteDecisionAsync(endpoint.Id);
        decision3.CanExecute.Should().BeTrue();
        decision3.IsProbe.Should().BeTrue();
        decision3.ProbeToken.Should().NotBeNull();
        (decision3.ProbeToken == decision1.ProbeToken).Should().BeFalse();

        // Worker 2 probe succeeds: circuit resets to Closed
        await cbManager.RecordSuccessAsync(endpoint.Id, decision3.ProbeToken);

        var refreshedEndpoint = await _dbContext.WebhookEndpoints
            .AsNoTracking()
            .FirstAsync(e => e.Id == endpoint.Id);

        refreshedEndpoint.CircuitState.Should().Be(EndpointCircuitState.Closed);
        refreshedEndpoint.HalfOpenProbeToken.Should().BeNull();

        // Stale Worker 1 wakes up and attempts to record failure with old token
        await cbManager.RecordFailureAsync(endpoint.Id, decision1.ProbeToken);

        // Endpoint remains Closed because Worker 1 was fenced out!
        var afterStaleAttempt = await _dbContext.WebhookEndpoints
            .AsNoTracking()
            .FirstAsync(e => e.Id == endpoint.Id);

        afterStaleAttempt.CircuitState.Should().Be(EndpointCircuitState.Closed);
    }

    [Fact]
    public async Task ConcurrentReplay_WithSameIdempotencyKey_ReturnsSameAuditWithoutConflict()
    {
        var tenantId = Guid.Parse(RelayApiFactory.TenantAId);
        var now = _timeProvider.GetUtcNow();

        var endpoint = new WebhookEndpoint(
            Guid.NewGuid(),
            tenantId,
            "Replay Endpoint",
            "https://httpbin.org/post",
            "secret",
            now);

        var evt = new Event(
            Guid.NewGuid(),
            tenantId,
            "payment.failed",
            "{\"id\":\"pay_1\"}",
            "hashpay1",
            "idem_pay_1",
            now);

        _dbContext.WebhookEndpoints.Add(endpoint);
        _dbContext.Events.Add(evt);
        await _dbContext.SaveChangesAsync();

        var delivery = new WebhookDelivery(
            Guid.NewGuid(),
            evt.Id,
            endpoint.Id,
            tenantId,
            now);

        delivery.DeadLetter(now, 500, "InternalError", "Target service crashed");
        _dbContext.WebhookDeliveries.Add(delivery);
        await _dbContext.SaveChangesAsync();

        using var scope1 = _serviceProvider.CreateScope();
        using var scope2 = _serviceProvider.CreateScope();

        var store1 = scope1.ServiceProvider.GetRequiredService<IWebhookDeliveryStore>();
        var store2 = scope2.ServiceProvider.GetRequiredService<IWebhookDeliveryStore>();

        var replayKey = "test-replay-key-concurrency";

        // Concurrently replay with same key
        var task1 = store1.ReplayDeliveryAsync(delivery.Id, tenantId, replayKey, "admin1", now);
        var task2 = store2.ReplayDeliveryAsync(delivery.Id, tenantId, replayKey, "admin2", now);

        var results = await Task.WhenAll(task1, task2);
        var audit1 = results[0];
        var audit2 = results[1];

        audit1.Should().NotBeNull();
        audit2.Should().NotBeNull();
        audit1.Id.Should().Be(audit2.Id, "Both requests must resolve to the exact same audit record");
        audit1.ReplayKey.Should().Be(replayKey);

        var auditsInDb = await _dbContext.WebhookDeliveryReplayAudits
            .Where(a => a.DeliveryId == delivery.Id)
            .ToListAsync();

        auditsInDb.Count.Should().Be(1);
    }

    [Fact]
    public async Task ReplayIdempotencyKey_ScopedToDelivery_AllowsSameKeyOnDifferentDeliveries()
    {
        var tenantId = Guid.Parse(RelayApiFactory.TenantAId);
        var now = _timeProvider.GetUtcNow();

        var endpoint = new WebhookEndpoint(
            Guid.NewGuid(),
            tenantId,
            "Multi Delivery Endpoint",
            "https://httpbin.org/post",
            "secret",
            now);

        var evt1 = new Event(
            Guid.NewGuid(),
            tenantId,
            "invoice.created.1",
            "{\"inv\":\"1\"}",
            "hashinv1",
            "idem_inv_1",
            now);

        var evt2 = new Event(
            Guid.NewGuid(),
            tenantId,
            "invoice.created.2",
            "{\"inv\":\"2\"}",
            "hashinv2",
            "idem_inv_2",
            now);

        _dbContext.WebhookEndpoints.Add(endpoint);
        _dbContext.Events.AddRange(evt1, evt2);
        await _dbContext.SaveChangesAsync();

        var delivery1 = new WebhookDelivery(Guid.NewGuid(), evt1.Id, endpoint.Id, tenantId, now);
        var delivery2 = new WebhookDelivery(Guid.NewGuid(), evt2.Id, endpoint.Id, tenantId, now);

        delivery1.DeadLetter(now, 500, "Error", "Failed");
        delivery2.DeadLetter(now, 500, "Error", "Failed");

        _dbContext.WebhookDeliveries.AddRange(delivery1, delivery2);
        await _dbContext.SaveChangesAsync();

        using var scope = _serviceProvider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IWebhookDeliveryStore>();

        var sharedKey = "batch-replay-operation-42";

        var audit1 = await store.ReplayDeliveryAsync(delivery1.Id, tenantId, sharedKey, "operator", now);
        var audit2 = await store.ReplayDeliveryAsync(delivery2.Id, tenantId, sharedKey, "operator", now);

        audit1.DeliveryId.Should().Be(delivery1.Id);
        audit2.DeliveryId.Should().Be(delivery2.Id);
        audit1.Id.Should().NotBe(audit2.Id);
        audit1.ReplayKey.Should().Be(sharedKey);
        audit2.ReplayKey.Should().Be(sharedKey);
    }
}
