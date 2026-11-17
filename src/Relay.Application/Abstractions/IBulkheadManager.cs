namespace Relay.Application.Abstractions;

public interface IBulkheadManager
{
    Task<IDisposable> AcquireSlotAsync(Guid endpointId, CancellationToken cancellationToken = default);
}
