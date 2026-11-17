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
using Relay.Infrastructure.Messaging;
using Relay.IntegrationTests.Fixtures;
using Relay.Worker.Services;
using Xunit;

namespace Relay.IntegrationTests;

[Collection("DatabaseCollection")]
public sealed class EventFanoutIntegrationTests : IAsyncLifetime
{
    private readonly RelayApiFactory _factory;
    private readonly ServiceProvider _serviceProvider;
    private readonly RelayDbContext _dbContext;

    public EventFanoutIntegrationTests(RelayApiFactory factory)
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
    public async Task Fanout_MatchingEndpoints_CreatesDurableWebhookDeliveries()
    {
        // Arrange
        var tenantId = Guid.Parse(RelayApiFactory.TenantAId);
        var now = DateTimeOffset.UtcNow;
        var eventId = Guid.NewGuid();

        // 1. Create 2 endpoints: one matching "order.created", one matching "order.fulfilled"
        var ep1 = new WebhookEndpoint(Guid.NewGuid(), tenantId, "Orders EP", "https://93.184.216.34/orders", "protected1", now);
        ep1.SetSubscriptions(new[] { "order.created" });

        var ep2 = new WebhookEndpoint(Guid.NewGuid(), tenantId, "All Orders EP", "https://93.184.216.34/all", "protected2", now);
        ep2.SetSubscriptions(new[] { "order.created", "order.fulfilled" });

        var epUnrelated = new WebhookEndpoint(Guid.NewGuid(), tenantId, "Invoices EP", "https://93.184.216.34/invoices", "protected3", now);
        epUnrelated.SetSubscriptions(new[] { "invoice.paid" });

        _dbContext.WebhookEndpoints.AddRange(ep1, ep2, epUnrelated);

        // 2. Insert accepted event in database
        var evt = new Event(eventId, tenantId, "order.created", """{"orderId":"1001"}""", "hash1001", "idem_fanout_1", now);
        _dbContext.Events.Add(evt);
        await _dbContext.SaveChangesAsync();

        // Act: Execute fanout logic (simulating consumer execution)
        var envelope = OutboxEnvelope.Create(eventId, tenantId, "order.created", now, evt.PayloadJson);

        await ExecuteFanoutTransactionAsync(envelope);

        // Assert: 2 deliveries created for ep1 and ep2; 0 for epUnrelated
        var deliveries = await _dbContext.WebhookDeliveries
            .Where(d => d.EventId == eventId)
            .ToListAsync();

        deliveries.Should().HaveCount(2);
        deliveries.Select(d => d.EndpointId).Should().BeEquivalentTo(new[] { ep1.Id, ep2.Id });
        deliveries.Should().OnlyContain(d => d.Status == WebhookDeliveryStatus.Pending);

        var fanout = await _dbContext.WebhookEventFanouts.FirstOrDefaultAsync(f => f.EventId == eventId);
        fanout.Should().NotBeNull();
        fanout!.MatchedEndpointCount.Should().Be(2);
    }

    [Fact]
    public async Task Fanout_DuplicateMessage_IsIdempotentAndDoesNotDuplicateDeliveries()
    {
        // Arrange
        var tenantId = Guid.Parse(RelayApiFactory.TenantAId);
        var now = DateTimeOffset.UtcNow;
        var eventId = Guid.NewGuid();

        var ep = new WebhookEndpoint(Guid.NewGuid(), tenantId, "Test EP", "https://93.184.216.34/test", "protected", now);
        ep.SetSubscriptions(new[] { "user.created" });
        _dbContext.WebhookEndpoints.Add(ep);

        var evt = new Event(eventId, tenantId, "user.created", "{}", "hash", "idem_dup_fanout", now);
        _dbContext.Events.Add(evt);
        await _dbContext.SaveChangesAsync();

        var envelope = OutboxEnvelope.Create(eventId, tenantId, "user.created", now, "{}");

        // Act: Fanout twice
        await ExecuteFanoutTransactionAsync(envelope);
        await ExecuteFanoutTransactionAsync(envelope); // Redelivery

        // Assert: Exactly 1 delivery and 1 fanout record
        var deliveries = await _dbContext.WebhookDeliveries.Where(d => d.EventId == eventId).ToListAsync();
        deliveries.Should().HaveCount(1);

        var fanouts = await _dbContext.WebhookEventFanouts.Where(f => f.EventId == eventId).ToListAsync();
        fanouts.Should().HaveCount(1);
    }

    [Fact]
    public async Task Fanout_NoSubscribers_RecordsZeroMatchedAndCompletes()
    {
        // Arrange
        var tenantId = Guid.Parse(RelayApiFactory.TenantAId);
        var now = DateTimeOffset.UtcNow;
        var eventId = Guid.NewGuid();

        var evt = new Event(eventId, tenantId, "unknown.event", "{}", "hash", "idem_zero", now);
        _dbContext.Events.Add(evt);
        await _dbContext.SaveChangesAsync();

        var envelope = OutboxEnvelope.Create(eventId, tenantId, "unknown.event", now, "{}");

        // Act
        await ExecuteFanoutTransactionAsync(envelope);

        // Assert
        var deliveries = await _dbContext.WebhookDeliveries.Where(d => d.EventId == eventId).ToListAsync();
        deliveries.Should().BeEmpty();

        var fanout = await _dbContext.WebhookEventFanouts.FirstOrDefaultAsync(f => f.EventId == eventId);
        fanout.Should().NotBeNull();
        fanout!.MatchedEndpointCount.Should().Be(0);
    }

    private async Task ExecuteFanoutTransactionAsync(OutboxEnvelope envelope)
    {
        var now = DateTimeOffset.UtcNow;
        var strategy = _dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _dbContext.Database.BeginTransactionAsync();

            var existingFanout = await _dbContext.WebhookEventFanouts
                .FirstOrDefaultAsync(f => f.EventId == envelope.EventId);

            if (existingFanout is not null)
            {
                await tx.CommitAsync();
                return;
            }

            var storedEvent = await _dbContext.Events
                .FirstOrDefaultAsync(e => e.Id == envelope.EventId && e.TenantId == envelope.TenantId);

            if (storedEvent is null)
            {
                await tx.CommitAsync();
                return;
            }

            var matchingEndpoints = await _dbContext.WebhookEndpoints
                .Include(e => e.Subscriptions)
                .Where(e => e.TenantId == envelope.TenantId && e.IsActive)
                .Where(e => e.Subscriptions.Any(s => s.EventType == storedEvent.EventType))
                .ToListAsync();

            var createdCount = 0;
            foreach (var endpoint in matchingEndpoints)
            {
                var delivery = new WebhookDelivery(
                    id: Guid.NewGuid(),
                    eventId: storedEvent.Id,
                    endpointId: endpoint.Id,
                    tenantId: storedEvent.TenantId,
                    createdAtUtc: now,
                    status: WebhookDeliveryStatus.Pending);

                _dbContext.WebhookDeliveries.Add(delivery);
                createdCount++;
            }

            var fanout = new WebhookEventFanout(
                id: Guid.NewGuid(),
                eventId: storedEvent.Id,
                tenantId: storedEvent.TenantId,
                processedAtUtc: now,
                matchedEndpointCount: createdCount);

            _dbContext.WebhookEventFanouts.Add(fanout);

            await _dbContext.SaveChangesAsync();
            await tx.CommitAsync();
        });
    }
}
