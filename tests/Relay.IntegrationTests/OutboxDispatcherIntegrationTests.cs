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
using Relay.Infrastructure.Persistence;
using Relay.IntegrationTests.Fixtures;
using Relay.Worker.Options;
using Relay.Worker.Services;
using Xunit;

namespace Relay.IntegrationTests;

[Collection("DatabaseCollection")]
public sealed class OutboxDispatcherIntegrationTests : IAsyncLifetime
{
    private readonly RelayApiFactory _factory;
    private readonly ServiceProvider _serviceProvider;
    private readonly RelayDbContext _dbContext;

    public OutboxDispatcherIntegrationTests(RelayApiFactory factory)
    {
        _factory = factory;

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
    public async Task ClaimBatchAsync_ConcurrentWorkers_NeverClaimSameRecords()
    {
        // Arrange
        var tenantId = Guid.Parse(RelayApiFactory.TenantAId);
        var now = DateTimeOffset.UtcNow;

        // Seed 20 pending outbox messages
        for (var i = 0; i < 20; i++)
        {
            var eventId = Guid.NewGuid();
            var evt = new Event(eventId, tenantId, "order.placed", "{}", "hash", $"idem_{i}", now);
            var msg = new OutboxMessage(Guid.NewGuid(), eventId, tenantId, "order.placed", "{}", now);
            _dbContext.Events.Add(evt);
            _dbContext.OutboxMessages.Add(msg);
        }
        await _dbContext.SaveChangesAsync();

        using var scope1 = _serviceProvider.CreateScope();
        using var scope2 = _serviceProvider.CreateScope();
        var store1 = scope1.ServiceProvider.GetRequiredService<IOutboxStore>();
        var store2 = scope2.ServiceProvider.GetRequiredService<IOutboxStore>();

        var lease1 = Guid.NewGuid();
        var lease2 = Guid.NewGuid();
        var leaseDuration = TimeSpan.FromMinutes(2);

        // Act: two workers race to claim batches of 10 concurrently
        var task1 = store1.ClaimBatchAsync(10, lease1, leaseDuration);
        var task2 = store2.ClaimBatchAsync(10, lease2, leaseDuration);

        var results = await Task.WhenAll(task1, task2);
        var batch1 = results[0];
        var batch2 = results[1];

        // Assert: the safety invariant. Under READPAST a worker skips rows another worker holds locked, and SQL Server
        // may lock every row it scans for a TOP (N) ... ORDER BY plan, so a racing worker can legitimately receive
        // fewer than N rows (even zero). What must never happen is the same record being claimed twice.
        batch1.Count.Should().BeInRange(0, 10);
        batch2.Count.Should().BeInRange(0, 10);

        var batch1Ids = batch1.Select(m => m.Id).ToHashSet();
        var batch2Ids = batch2.Select(m => m.Id).ToHashSet();

        batch1Ids.Overlaps(batch2Ids).Should().BeFalse("Concurrent workers using UPDLOCK/READPAST must never claim overlapping records");
        batch1.Should().OnlyContain(m => m.LeaseToken == lease1);
        batch2.Should().OnlyContain(m => m.LeaseToken == lease2);

        // Liveness: once the race is over, the two workers can drain the rest, and every message is claimed exactly once.
        var claimedIds = batch1Ids.Concat(batch2Ids).ToList();
        for (var attempt = 0; attempt < 5 && claimedIds.Count < 20; attempt++)
        {
            var more = await store1.ClaimBatchAsync(10, lease1, leaseDuration);
            claimedIds.AddRange(more.Select(m => m.Id));
        }

        claimedIds.Should().HaveCount(20);
        claimedIds.Distinct().Should().HaveCount(20, "every outbox message must be claimed exactly once");
    }

    [Fact]
    public async Task ClaimBatchAsync_ExpiredLease_IsReclaimedByNextWorker()
    {
        // Arrange
        var tenantId = Guid.Parse(RelayApiFactory.TenantAId);
        var now = DateTimeOffset.UtcNow;
        var eventId = Guid.NewGuid();
        var outboxId = Guid.NewGuid();

        var evt = new Event(eventId, tenantId, "user.signedup", "{}", "hash", "idem_expired", now);
        var msg = new OutboxMessage(outboxId, eventId, tenantId, "user.signedup", "{}", now);
        _dbContext.Events.Add(evt);
        _dbContext.OutboxMessages.Add(msg);
        await _dbContext.SaveChangesAsync();

        var store = _serviceProvider.GetRequiredService<IOutboxStore>();

        // Worker 1 claims with very short / negative lease (simulating expired lease)
        var lease1 = Guid.NewGuid();
        var claimedFirst = await store.ClaimBatchAsync(1, lease1, TimeSpan.FromMilliseconds(-1000));
        claimedFirst.Should().HaveCount(1);
        claimedFirst[0].Id.Should().Be(outboxId);

        // Act: Worker 2 attempts to claim
        var lease2 = Guid.NewGuid();
        var claimedSecond = await store.ClaimBatchAsync(1, lease2, TimeSpan.FromMinutes(1));

        // Assert: Worker 2 successfully reclaims the abandoned record
        claimedSecond.Should().HaveCount(1);
        claimedSecond[0].Id.Should().Be(outboxId);
        claimedSecond[0].LeaseToken.Should().Be(lease2);
        claimedSecond[0].ClaimCount.Should().Be(2);
        claimedSecond[0].PublishAttemptCount.Should().Be(0);
    }

    [Fact]
    public async Task MarkPublishedAsync_StaleWorkerWithExpiredLease_FailsToOverwriteNewOwner()
    {
        // Arrange
        var tenantId = Guid.Parse(RelayApiFactory.TenantAId);
        var now = DateTimeOffset.UtcNow;
        var eventId = Guid.NewGuid();
        var outboxId = Guid.NewGuid();

        var evt = new Event(eventId, tenantId, "account.verified", "{}", "hash", "idem_fencing", now);
        var msg = new OutboxMessage(outboxId, eventId, tenantId, "account.verified", "{}", now);
        _dbContext.Events.Add(evt);
        _dbContext.OutboxMessages.Add(msg);
        await _dbContext.SaveChangesAsync();

        var store = _serviceProvider.GetRequiredService<IOutboxStore>();

        // Worker 1 claims with expired lease
        var lease1 = Guid.NewGuid();
        await store.ClaimBatchAsync(1, lease1, TimeSpan.FromMilliseconds(-1000));

        // Worker 2 claims it because lease1 expired
        var lease2 = Guid.NewGuid();
        await store.ClaimBatchAsync(1, lease2, TimeSpan.FromMinutes(5));

        // Act: Stale Worker 1 wakes up and tries to MarkPublished with lease1
        await store.MarkPublishedAsync(outboxId, lease1, DateTimeOffset.UtcNow);

        // Assert: DB record must still belong to Worker 2 and remain Processing
        var recordInDb = await _dbContext.OutboxMessages.AsNoTracking().FirstAsync(m => m.Id == outboxId);
        recordInDb.Status.Should().Be(OutboxStatus.Processing);
        recordInDb.LeaseToken.Should().Be(lease2);
        recordInDb.PublishedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task OutboxDispatcher_DispatchesBatchAndUpdatesPublishedStatus()
    {
        // Arrange
        var tenantId = Guid.Parse(RelayApiFactory.TenantAId);
        var now = DateTimeOffset.UtcNow;
        var eventId = Guid.NewGuid();
        var outboxId = Guid.NewGuid();

        var evt = new Event(eventId, tenantId, "payment.completed", """{"paymentId":"PAY-123"}""", "hash", "idem_pub", now);
        var msg = new OutboxMessage(outboxId, eventId, tenantId, "payment.completed", """{"paymentId":"PAY-123"}""", now);
        _dbContext.Events.Add(evt);
        _dbContext.OutboxMessages.Add(msg);
        await _dbContext.SaveChangesAsync();

        var fakePublisher = new TestRecordingPublisher();
        var store = _serviceProvider.GetRequiredService<IOutboxStore>();
        var options = Options.Create(new OutboxDispatcherOptions
        {
            BatchSize = 10,
            MaxConcurrency = 2,
            LeaseDuration = TimeSpan.FromMinutes(1)
        });

        var dispatcher = new OutboxDispatcher(
            store,
            fakePublisher,
            TimeProvider.System,
            options,
            NullLogger<OutboxDispatcher>.Instance);

        // Act
        var processed = await dispatcher.ProcessBatchAsync();

        // Assert
        processed.Should().Be(1);
        fakePublisher.PublishedEnvelopes.Should().HaveCount(1);
        var published = fakePublisher.PublishedEnvelopes[0];
        published.EventId.Should().Be(eventId);
        published.EventType.Should().Be("payment.completed");

        var dbRecord = await _dbContext.OutboxMessages.AsNoTracking().FirstAsync(m => m.Id == outboxId);
        dbRecord.Status.Should().Be(OutboxStatus.Published);
        dbRecord.PublishedAtUtc.Should().NotBeNull();
        dbRecord.LeaseToken.Should().BeNull();
    }

    [Fact]
    public async Task OutboxDispatcher_OnTransientError_SchedulesRetryWithFutureTimestamp()
    {
        // Arrange
        var tenantId = Guid.Parse(RelayApiFactory.TenantAId);
        var now = DateTimeOffset.UtcNow;
        var eventId = Guid.NewGuid();
        var outboxId = Guid.NewGuid();

        var evt = new Event(eventId, tenantId, "webhook.retry", """{"attempt":1}""", "hash", "idem_retry", now);
        var msg = new OutboxMessage(outboxId, eventId, tenantId, "webhook.retry", """{"attempt":1}""", now);
        _dbContext.Events.Add(evt);
        _dbContext.OutboxMessages.Add(msg);
        await _dbContext.SaveChangesAsync();

        var failingPublisher = new TestFailingPublisher(new TimeoutException("Service Bus connection timeout"));
        var store = _serviceProvider.GetRequiredService<IOutboxStore>();
        var options = Options.Create(new OutboxDispatcherOptions
        {
            BatchSize = 10,
            MaxRetryAttempts = 3,
            BaseRetryDelay = TimeSpan.FromSeconds(5),
            MaxRetryDelay = TimeSpan.FromMinutes(1)
        });

        var dispatcher = new OutboxDispatcher(
            store,
            failingPublisher,
            TimeProvider.System,
            options,
            NullLogger<OutboxDispatcher>.Instance);

        // Act
        var processed = await dispatcher.ProcessBatchAsync();

        // Assert
        processed.Should().Be(1);
        var dbRecord = await _dbContext.OutboxMessages.AsNoTracking().FirstAsync(m => m.Id == outboxId);
        dbRecord.Status.Should().Be(OutboxStatus.Pending);
        dbRecord.NextAttemptAtUtc.Should().BeAfter(now);
        dbRecord.LastError.Should().Contain("TimeoutException");
        dbRecord.ClaimCount.Should().Be(1);
        dbRecord.PublishAttemptCount.Should().Be(1);
        dbRecord.LeaseToken.Should().BeNull();
    }

    private sealed class TestRecordingPublisher : IEventMessagePublisher
    {
        public List<OutboxEnvelope> PublishedEnvelopes { get; } = new();

        public Task PublishAsync(OutboxEnvelope envelope, CancellationToken cancellationToken = default)
        {
            PublishedEnvelopes.Add(envelope);
            return Task.CompletedTask;
        }
    }

    private sealed class TestFailingPublisher : IEventMessagePublisher
    {
        private readonly Exception _exceptionToThrow;

        public TestFailingPublisher(Exception exceptionToThrow)
        {
            _exceptionToThrow = exceptionToThrow;
        }

        public Task PublishAsync(OutboxEnvelope envelope, CancellationToken cancellationToken = default)
        {
            throw _exceptionToThrow;
        }
    }
}
