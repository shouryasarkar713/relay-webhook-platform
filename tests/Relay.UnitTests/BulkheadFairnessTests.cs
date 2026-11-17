using FluentAssertions;
using Microsoft.Extensions.Options;
using Relay.Application.Options;
using Relay.Infrastructure.Services;
using Xunit;

namespace Relay.UnitTests;

public sealed class BulkheadFairnessTests
{
    [Fact]
    public async Task AcquireSlotAsync_InvertedOrder_PreventsStarvationOfEndpointBByEndpointA()
    {
        // Global limit = 4, Per-endpoint limit = 2
        var options = new BulkheadOptions
        {
            MaxGlobalConcurrency = 4,
            MaxPerEndpointConcurrency = 2
        };

        using var bulkhead = new EndpointBulkheadManager(Options.Create(options));
        var endpointA = Guid.NewGuid();
        var endpointB = Guid.NewGuid();

        // Endpoint A takes its 2 available permits
        var leaseA1 = await bulkhead.AcquireSlotAsync(endpointA);
        var leaseA2 = await bulkhead.AcquireSlotAsync(endpointA);

        // Endpoint A launches 5 additional queued attempts that will block on Endpoint A's semaphore
        var queuedTasksEndpointA = new List<Task>();
        using var cancelQueued = new CancellationTokenSource();
        for (int i = 0; i < 5; i++)
        {
            queuedTasksEndpointA.Add(bulkhead.AcquireSlotAsync(endpointA, cancelQueued.Token));
        }

        // Endpoint B arrives. Even though Endpoint A has 5 pending waiters,
        // those waiters are queued on Endpoint A's semaphore and have NOT consumed the global permits.
        // Endpoint B should be able to acquire immediately within 200ms.
        using var ctsEndpointB = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var leaseB1 = await bulkhead.AcquireSlotAsync(endpointB, ctsEndpointB.Token);
        var leaseB2 = await bulkhead.AcquireSlotAsync(endpointB, ctsEndpointB.Token);

        leaseB1.Should().NotBeNull();
        leaseB2.Should().NotBeNull();

        // Clean up
        cancelQueued.Cancel();
        leaseA1.Dispose();
        leaseA2.Dispose();
        leaseB1.Dispose();
        leaseB2.Dispose();
    }

    [Fact]
    public async Task SweepInactiveEndpoints_EvictsIdleSemaphores_PreservesActiveHolders()
    {
        var options = new BulkheadOptions
        {
            MaxGlobalConcurrency = 10,
            MaxPerEndpointConcurrency = 2
        };

        using var bulkhead = new EndpointBulkheadManager(Options.Create(options));
        var activeEndpoint = Guid.NewGuid();
        var idleEndpoint = Guid.NewGuid();

        var activeLease = await bulkhead.AcquireSlotAsync(activeEndpoint);
        var idleLease = await bulkhead.AcquireSlotAsync(idleEndpoint);

        // Release the idle endpoint lease
        idleLease.Dispose();

        // Wait a small duration and sweep with a 10ms threshold
        await Task.Delay(25);
        bulkhead.SweepInactiveEndpoints(TimeSpan.FromMilliseconds(10));

        // Active lease should still function cleanly and be able to release without error
        activeLease.Dispose();

        // Idle endpoint can be re-acquired cleanly (creates a new holder)
        using var reacquiredLease = await bulkhead.AcquireSlotAsync(idleEndpoint);
        reacquiredLease.Should().NotBeNull();
    }

    [Fact]
    public async Task AcquireSlotAsync_ConcurrentSweeperAndWorkerRace_NeverThrowsObjectDisposedException()
    {
        var options = new BulkheadOptions
        {
            MaxGlobalConcurrency = 50,
            MaxPerEndpointConcurrency = 10
        };

        using var bulkhead = new EndpointBulkheadManager(Options.Create(options));
        var endpointId = Guid.NewGuid();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var exceptions = new List<Exception>();

        // 5 worker tasks racing to acquire and release
        var workerTasks = Enumerable.Range(0, 5).Select(_ => Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    using var lease = await bulkhead.AcquireSlotAsync(endpointId, cts.Token);
                    await Task.Yield();
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    lock (exceptions)
                    {
                        exceptions.Add(ex);
                    }
                }
            }
        })).ToList();

        // 1 sweeper task continuously sweeping with zero timeout
        var sweeperTask = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    bulkhead.SweepInactiveEndpoints(TimeSpan.Zero);
                    Thread.Sleep(1);
                }
                catch (Exception ex)
                {
                    lock (exceptions)
                    {
                        exceptions.Add(ex);
                    }
                }
            }
        });

        await Task.WhenAll(workerTasks.Concat(new[] { sweeperTask }));

        exceptions.Should().BeEmpty("Sweeper and Worker race must never throw ObjectDisposedException or corrupt semaphore state");
    }
}
