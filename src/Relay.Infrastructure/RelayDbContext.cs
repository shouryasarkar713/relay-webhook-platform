using Microsoft.EntityFrameworkCore;
using Relay.Application.Abstractions;
using Relay.Domain.Entities;

namespace Relay.Infrastructure;

public sealed class RelayDbContext : DbContext, IRelayDbContext
{
    public RelayDbContext(DbContextOptions<RelayDbContext> options) : base(options) { }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<WebhookEndpoint> WebhookEndpoints => Set<WebhookEndpoint>();
    public DbSet<WebhookEndpointSubscription> WebhookEndpointSubscriptions => Set<WebhookEndpointSubscription>();
    public DbSet<WebhookEventFanout> WebhookEventFanouts => Set<WebhookEventFanout>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();
    public DbSet<WebhookDeliveryAttempt> WebhookDeliveryAttempts => Set<WebhookDeliveryAttempt>();
    public DbSet<WebhookDeliveryReplayAudit> WebhookDeliveryReplayAudits => Set<WebhookDeliveryReplayAudit>();

    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        var strategy = Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await Database.BeginTransactionAsync(cancellationToken);
            var result = await operation(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        });
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(RelayDbContext).Assembly);
    }
}
