using Microsoft.EntityFrameworkCore;
using Relay.Domain.Entities;

namespace Relay.Application.Abstractions;

public interface IRelayDbContext
{
    DbSet<Tenant> Tenants { get; }
    DbSet<ApiKey> ApiKeys { get; }
    DbSet<Event> Events { get; }
    DbSet<OutboxMessage> OutboxMessages { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    Task<IDbTransactionContext> BeginTransactionAsync(CancellationToken cancellationToken = default);
}
