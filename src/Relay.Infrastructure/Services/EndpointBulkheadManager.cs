using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Relay.Application.Abstractions;
using Relay.Application.Options;

namespace Relay.Infrastructure.Services;

public sealed class EndpointBulkheadManager : IBulkheadManager, IDisposable
{
    private readonly SemaphoreSlim _globalSemaphore;
    private readonly ConcurrentDictionary<Guid, EndpointSemaphoreHolder> _endpointSemaphores = new();
    private readonly BulkheadOptions _options;
    private readonly TimeProvider _timeProvider;
    private bool _disposed;

    public EndpointBulkheadManager(IOptions<BulkheadOptions> options, TimeProvider? timeProvider = null)
    {
        _options = options.Value;
        _timeProvider = timeProvider ?? TimeProvider.System;
        var globalMax = Math.Max(1, _options.MaxGlobalConcurrency);
        _globalSemaphore = new SemaphoreSlim(globalMax, globalMax);
    }

    public int ActiveEndpointCount => _endpointSemaphores.Count;
    public int AvailableGlobalPermits => _globalSemaphore.CurrentCount;

    public async Task<IDisposable> AcquireSlotAsync(Guid endpointId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var endpointMax = Math.Max(1, _options.MaxPerEndpointConcurrency);
        var holder = GetOrAddHolder(endpointId, endpointMax);

        // 1. ADMISSION CONTROL: Acquire per-endpoint concurrency slot FIRST.
        // This ensures that queued requests for one saturated endpoint wait in that endpoint's queue
        // and CANNOT consume global execution slots, completely preventing noisy-neighbor starvation.
        await holder.Semaphore.WaitAsync(cancellationToken);

        try
        {
            // 2. EXECUTION CAPACITY: Acquire global worker concurrency slot SECOND.
            await _globalSemaphore.WaitAsync(cancellationToken);
        }
        catch
        {
            // If global acquire fails or cancels, immediately release endpoint slot to avoid deadlock
            holder.Semaphore.Release();
            holder.DecrementActive();
            throw;
        }

        return new BulkheadLease(this, endpointId, _globalSemaphore, holder);
    }

    public int SweepInactiveEndpoints(TimeSpan idleTimeout)
    {
        if (_disposed) return 0;

        var now = _timeProvider.GetUtcNow();
        var endpointMax = Math.Max(1, _options.MaxPerEndpointConcurrency);
        var sweptCount = 0;

        foreach (var kvp in _endpointSemaphores)
        {
            var holder = kvp.Value;
            if (holder.ActiveCount == 0 &&
                holder.Semaphore.CurrentCount == endpointMax &&
                now - holder.LastUsedAtUtc >= idleTimeout)
            {
                // Atomically remove from dictionary first
                if (_endpointSemaphores.TryRemove(new KeyValuePair<Guid, EndpointSemaphoreHolder>(kvp.Key, holder)))
                {
                    if (holder.TryMarkDisposed())
                    {
                        holder.Semaphore.Dispose();
                        sweptCount++;
                    }
                    else
                    {
                        // Active worker acquired it just before disposal; put back
                        _endpointSemaphores.TryAdd(kvp.Key, holder);
                    }
                }
            }
        }

        return sweptCount;
    }

    public void ReleaseEndpoint(Guid endpointId)
    {
        if (_endpointSemaphores.TryRemove(endpointId, out var holder))
        {
            if (holder.TryMarkDisposed())
            {
                holder.Semaphore.Dispose();
            }
        }
    }

    private EndpointSemaphoreHolder GetOrAddHolder(Guid endpointId, int endpointMax)
    {
        while (true)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var holder = _endpointSemaphores.GetOrAdd(
                endpointId,
                id => new EndpointSemaphoreHolder(endpointMax, _timeProvider.GetUtcNow()));

            if (holder.TryIncrementActive())
            {
                return holder;
            }

            // If holder was marked disposed during a sweep, remove stale reference and loop to recreate
            _endpointSemaphores.TryRemove(new KeyValuePair<Guid, EndpointSemaphoreHolder>(endpointId, holder));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _globalSemaphore.Dispose();
        foreach (var holder in _endpointSemaphores.Values)
        {
            holder.TryMarkDisposed();
            holder.Semaphore.Dispose();
        }
        _endpointSemaphores.Clear();
    }

    private sealed class EndpointSemaphoreHolder
    {
        public SemaphoreSlim Semaphore { get; }
        public DateTimeOffset LastUsedAtUtc { get; set; }
        private int _activeCount;
        private int _isDisposed;

        public EndpointSemaphoreHolder(int maxConcurrency, DateTimeOffset now)
        {
            Semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
            LastUsedAtUtc = now;
            _activeCount = 0;
            _isDisposed = 0;
        }

        public int ActiveCount => Volatile.Read(ref _activeCount);

        public bool TryIncrementActive()
        {
            while (true)
            {
                if (Volatile.Read(ref _isDisposed) == 1)
                {
                    return false;
                }

                var current = Volatile.Read(ref _activeCount);
                if (Interlocked.CompareExchange(ref _activeCount, current + 1, current) == current)
                {
                    // Re-check disposal state after incrementing to prevent race with TryMarkDisposed
                    if (Volatile.Read(ref _isDisposed) == 1)
                    {
                        Interlocked.Decrement(ref _activeCount);
                        return false;
                    }

                    return true;
                }
            }
        }

        public void DecrementActive()
        {
            Interlocked.Decrement(ref _activeCount);
        }

        public bool TryMarkDisposed()
        {
            // Atomically flag as disposed
            if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) != 0)
            {
                return false; // Already marked disposed
            }

            // Verify active count. If another thread incremented right before we set the flag, abort disposal.
            if (Volatile.Read(ref _activeCount) > 0)
            {
                Volatile.Write(ref _isDisposed, 0); // Revert
                return false;
            }

            return true;
        }
    }

    private sealed class BulkheadLease : IDisposable
    {
        private readonly EndpointBulkheadManager _manager;
        private readonly Guid _endpointId;
        private readonly SemaphoreSlim _global;
        private readonly EndpointSemaphoreHolder _holder;
        private int _released;

        public BulkheadLease(
            EndpointBulkheadManager manager,
            Guid endpointId,
            SemaphoreSlim global,
            EndpointSemaphoreHolder holder)
        {
            _manager = manager;
            _endpointId = endpointId;
            _global = global;
            _holder = holder;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                // Release execution capacity first, then admission slot
                _global.Release();
                _holder.Semaphore.Release();
                _holder.DecrementActive();
                _holder.LastUsedAtUtc = _manager._timeProvider.GetUtcNow();
            }
        }
    }
}
