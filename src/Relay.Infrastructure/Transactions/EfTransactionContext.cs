using Microsoft.EntityFrameworkCore.Storage;
using Relay.Application.Abstractions;

namespace Relay.Infrastructure.Transactions;

public sealed class EfTransactionContext : IDbTransactionContext
{
    private readonly IDbContextTransaction _transaction;

    public EfTransactionContext(IDbContextTransaction transaction)
    {
        _transaction = transaction;
    }

    public Task CommitAsync(CancellationToken cancellationToken = default) =>
        _transaction.CommitAsync(cancellationToken);

    public Task RollbackAsync(CancellationToken cancellationToken = default) =>
        _transaction.RollbackAsync(cancellationToken);

    public ValueTask DisposeAsync() => _transaction.DisposeAsync();

    public void Dispose() => _transaction.Dispose();
}
