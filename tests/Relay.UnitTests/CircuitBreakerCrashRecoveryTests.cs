using FluentAssertions;
using Relay.Domain.Entities;
using Relay.Domain.Enums;
using Xunit;

namespace Relay.UnitTests;

public sealed class CircuitBreakerCrashRecoveryTests
{
    private static WebhookEndpoint CreateEndpointInHalfOpen(DateTimeOffset now)
    {
        var endpoint = new WebhookEndpoint(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Crash Test Endpoint",
            "https://api.example.com/webhook",
            "protected-secret",
            now);

        // Trip to open
        endpoint.RecordDeliveryFailure(1, TimeSpan.FromSeconds(60), now);
        endpoint.CircuitState.Should().Be(EndpointCircuitState.Open);

        // Advance beyond open duration to evaluate to HalfOpen
        endpoint.EvaluateCircuitState(TimeSpan.FromSeconds(60), now.AddSeconds(61));
        endpoint.CircuitState.Should().Be(EndpointCircuitState.HalfOpen);

        return endpoint;
    }

    [Fact]
    public void TryAcquireHalfOpenProbe_ClaimsLease_AndBlocksConcurrentWorker()
    {
        var now = DateTimeOffset.UtcNow;
        var endpoint = CreateEndpointInHalfOpen(now);

        var worker1Token = Guid.NewGuid();
        var leaseDuration = TimeSpan.FromSeconds(30);

        // Worker 1 acquires probe
        var acquired1 = endpoint.TryAcquireHalfOpenProbe(worker1Token, leaseDuration, now);
        acquired1.Should().BeTrue();
        endpoint.HalfOpenProbeToken.Should().Be(worker1Token);
        endpoint.HalfOpenProbeLeaseExpiresAtUtc.Should().Be(now.Add(leaseDuration));

        // Worker 2 attempts concurrent probe acquisition before lease expires
        var worker2Token = Guid.NewGuid();
        var acquired2 = endpoint.TryAcquireHalfOpenProbe(worker2Token, leaseDuration, now.AddSeconds(10));
        acquired2.Should().BeFalse();
        endpoint.HalfOpenProbeToken.Should().Be(worker1Token);
    }

    [Fact]
    public void TryAcquireHalfOpenProbe_ExpiredLeaseFromCrashedWorker_AllowsRecovery()
    {
        var now = DateTimeOffset.UtcNow;
        var endpoint = CreateEndpointInHalfOpen(now);

        var worker1Token = Guid.NewGuid();
        var leaseDuration = TimeSpan.FromSeconds(30);

        // Worker 1 acquires probe and crashes (leaves probe active)
        endpoint.TryAcquireHalfOpenProbe(worker1Token, leaseDuration, now);

        // Time advances beyond lease expiration
        var afterExpiry = now.AddSeconds(31);
        var worker2Token = Guid.NewGuid();

        // Worker 2 should be able to recover and claim the expired probe
        var acquired2 = endpoint.TryAcquireHalfOpenProbe(worker2Token, leaseDuration, afterExpiry);
        acquired2.Should().BeTrue();
        endpoint.HalfOpenProbeToken.Should().Be(worker2Token);
        endpoint.HalfOpenProbeLeaseExpiresAtUtc.Should().Be(afterExpiry.Add(leaseDuration));
    }

    [Fact]
    public void RecordDeliverySuccess_WithFencing_RejectsStaleProbeOwner()
    {
        var now = DateTimeOffset.UtcNow;
        var endpoint = CreateEndpointInHalfOpen(now);

        var staleWorkerToken = Guid.NewGuid();
        var activeWorkerToken = Guid.NewGuid();
        var leaseDuration = TimeSpan.FromSeconds(30);

        // Worker 1 claims probe
        endpoint.TryAcquireHalfOpenProbe(staleWorkerToken, leaseDuration, now);

        // Lease expires and Worker 2 claims probe
        endpoint.TryAcquireHalfOpenProbe(activeWorkerToken, leaseDuration, now.AddSeconds(31));
        endpoint.HalfOpenProbeToken.Should().Be(activeWorkerToken);

        // Stale Worker 1 attempts to record success
        endpoint.RecordDeliverySuccess(now.AddSeconds(35), expectedProbeToken: staleWorkerToken);

        // Circuit should still be HalfOpen because Worker 1 was fenced out
        endpoint.CircuitState.Should().Be(EndpointCircuitState.HalfOpen);
        endpoint.HalfOpenProbeToken.Should().Be(activeWorkerToken);

        // Active Worker 2 records success
        endpoint.RecordDeliverySuccess(now.AddSeconds(36), expectedProbeToken: activeWorkerToken);

        // Circuit properly transitions to Closed
        endpoint.CircuitState.Should().Be(EndpointCircuitState.Closed);
        endpoint.HalfOpenProbeToken.Should().BeNull();
        endpoint.ConsecutiveFailureCount.Should().Be(0);
    }

    [Fact]
    public void RecordDeliveryFailure_WithFencing_RejectsStaleProbeOwner()
    {
        var now = DateTimeOffset.UtcNow;
        var endpoint = CreateEndpointInHalfOpen(now);

        var staleWorkerToken = Guid.NewGuid();
        var activeWorkerToken = Guid.NewGuid();
        var leaseDuration = TimeSpan.FromSeconds(30);

        endpoint.TryAcquireHalfOpenProbe(staleWorkerToken, leaseDuration, now);
        endpoint.TryAcquireHalfOpenProbe(activeWorkerToken, leaseDuration, now.AddSeconds(31));

        // Stale Worker 1 failure is rejected
        endpoint.RecordDeliveryFailure(5, TimeSpan.FromSeconds(60), now.AddSeconds(35), expectedProbeToken: staleWorkerToken);
        endpoint.CircuitState.Should().Be(EndpointCircuitState.HalfOpen);

        // Active Worker 2 failure trips circuit back to Open
        endpoint.RecordDeliveryFailure(5, TimeSpan.FromSeconds(60), now.AddSeconds(36), expectedProbeToken: activeWorkerToken);
        endpoint.CircuitState.Should().Be(EndpointCircuitState.Open);
        endpoint.HalfOpenProbeToken.Should().BeNull();
    }
}
