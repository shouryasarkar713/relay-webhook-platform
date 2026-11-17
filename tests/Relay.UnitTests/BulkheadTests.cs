using FluentAssertions;
using Microsoft.Extensions.Options;
using Relay.Application.Options;
using Relay.Infrastructure.Services;
using Xunit;

namespace Relay.UnitTests;

public sealed class BulkheadTests
{
    [Fact]
    public async Task AcquireSlotAsync_RespectsPerEndpointConcurrencyLimit()
    {
        var options = new BulkheadOptions
        {
            MaxGlobalConcurrency = 10,
            MaxPerEndpointConcurrency = 2
        };

        using var bulkhead = new EndpointBulkheadManager(Options.Create(options));
        var endpointId = Guid.NewGuid();

        // Acquire 2 slots (the max per endpoint)
        var lease1 = await bulkhead.AcquireSlotAsync(endpointId);
        var lease2 = await bulkhead.AcquireSlotAsync(endpointId);

        // 3rd attempt should block
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var thirdAttemptTask = bulkhead.AcquireSlotAsync(endpointId, cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => thirdAttemptTask);

        // Releasing lease1 allows a new slot to be acquired
        lease1.Dispose();

        using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var lease3 = await bulkhead.AcquireSlotAsync(endpointId, cts2.Token);
        lease3.Should().NotBeNull();

        lease2.Dispose();
        lease3.Dispose();
    }

    [Fact]
    public async Task AcquireSlotAsync_RespectsGlobalConcurrencyLimit()
    {
        var options = new BulkheadOptions
        {
            MaxGlobalConcurrency = 3,
            MaxPerEndpointConcurrency = 5
        };

        using var bulkhead = new EndpointBulkheadManager(Options.Create(options));
        var ep1 = Guid.NewGuid();
        var ep2 = Guid.NewGuid();
        var ep3 = Guid.NewGuid();
        var ep4 = Guid.NewGuid();

        var lease1 = await bulkhead.AcquireSlotAsync(ep1);
        var lease2 = await bulkhead.AcquireSlotAsync(ep2);
        var lease3 = await bulkhead.AcquireSlotAsync(ep3);

        // 4th slot across different endpoints should be blocked by global limit
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var fourthAttemptTask = bulkhead.AcquireSlotAsync(ep4, cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fourthAttemptTask);

        lease1.Dispose();
        lease2.Dispose();
        lease3.Dispose();
    }
}
