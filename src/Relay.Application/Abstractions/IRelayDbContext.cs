using Microsoft.EntityFrameworkCore;
using Relay.Domain.Entities;

namespace Relay.Application.Abstractions;

public interface IRelayDbContext
{
    DbSet<Tenant> Tenants { get; }
    DbSet<ApiKey> ApiKeys { get; }
    DbSet<Event> Events { get; }
    DbSet<OutboxMessage> OutboxMessages { get; }
    DbSet<WebhookEndpoint> WebhookEndpoints { get; }
    DbSet<WebhookEndpointSubscription> WebhookEndpointSubscriptions { get; }
    DbSet<WebhookEventFanout> WebhookEventFanouts { get; }
    DbSet<WebhookDelivery> WebhookDeliveries { get; }
    DbSet<WebhookDeliveryAttempt> WebhookDeliveryAttempts { get; }
    DbSet<WebhookDeliveryReplayAudit> WebhookDeliveryReplayAudits { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default);
}
