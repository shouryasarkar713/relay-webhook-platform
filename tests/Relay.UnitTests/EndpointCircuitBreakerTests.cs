using FluentAssertions;
using Relay.Domain.Entities;
using Relay.Domain.Enums;
using Xunit;

namespace Relay.UnitTests;

public sealed class EndpointCircuitBreakerTests
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly DateTimeOffset _baseTime = new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
    private readonly TimeSpan _openDuration = TimeSpan.FromSeconds(60);
    private const int FailureThreshold = 5;

    [Fact]
    public void InitialState_IsClosedWithZeroFailures()
    {
        var endpoint = CreateEndpoint();

        endpoint.CircuitState.Should().Be(EndpointCircuitState.Closed);
        endpoint.ConsecutiveFailureCount.Should().Be(0);
        endpoint.CircuitOpenedAtUtc.Should().BeNull();
        endpoint.CircuitHalfOpenProbeActive.Should().BeFalse();
    }

    [Fact]
    public void FailuresBelowThreshold_IncrementCount_KeepStateClosed()
    {
        var endpoint = CreateEndpoint();

        for (int i = 1; i < FailureThreshold; i++)
        {
            endpoint.RecordDeliveryFailure(FailureThreshold, _openDuration, _baseTime.AddSeconds(i));
            endpoint.CircuitState.Should().Be(EndpointCircuitState.Closed);
            endpoint.ConsecutiveFailureCount.Should().Be(i);
        }
    }

    [Fact]
    public void FailuresReachingThreshold_TripsCircuitToOpen()
    {
        var endpoint = CreateEndpoint();
        var failureTime = _baseTime.AddSeconds(10);

        for (int i = 0; i < FailureThreshold; i++)
        {
            endpoint.RecordDeliveryFailure(FailureThreshold, _openDuration, failureTime);
        }

        endpoint.CircuitState.Should().Be(EndpointCircuitState.Open);
        endpoint.ConsecutiveFailureCount.Should().Be(FailureThreshold);
        endpoint.CircuitOpenedAtUtc.Should().Be(failureTime);
    }

    [Fact]
    public void EvaluateCircuitState_BeforeOpenDurationElapses_RemainsOpen()
    {
        var endpoint = CreateEndpoint();
        for (int i = 0; i < FailureThreshold; i++)
        {
            endpoint.RecordDeliveryFailure(FailureThreshold, _openDuration, _baseTime);
        }

        // 30 seconds elapsed (< 60s openDuration)
        endpoint.EvaluateCircuitState(_openDuration, _baseTime.AddSeconds(30));

        endpoint.CircuitState.Should().Be(EndpointCircuitState.Open);
    }

    [Fact]
    public void EvaluateCircuitState_AfterOpenDurationElapses_TransitionsToHalfOpen()
    {
        var endpoint = CreateEndpoint();
        for (int i = 0; i < FailureThreshold; i++)
        {
            endpoint.RecordDeliveryFailure(FailureThreshold, _openDuration, _baseTime);
        }

        // 61 seconds elapsed (> 60s openDuration)
        endpoint.EvaluateCircuitState(_openDuration, _baseTime.AddSeconds(61));

        endpoint.CircuitState.Should().Be(EndpointCircuitState.HalfOpen);
        endpoint.CircuitHalfOpenProbeActive.Should().BeFalse();
    }

    [Fact]
    public void TryAcquireHalfOpenProbe_AllowsOnlySingleConcurrentProbe()
    {
        var endpoint = CreateEndpoint();
        for (int i = 0; i < FailureThreshold; i++)
        {
            endpoint.RecordDeliveryFailure(FailureThreshold, _openDuration, _baseTime);
        }
        endpoint.EvaluateCircuitState(_openDuration, _baseTime.AddSeconds(65));

        // First probe acquires successfully
        var firstAcquired = endpoint.TryAcquireHalfOpenProbe();
        firstAcquired.Should().BeTrue();
        endpoint.CircuitHalfOpenProbeActive.Should().BeTrue();

        // Second concurrent probe is blocked
        var secondAcquired = endpoint.TryAcquireHalfOpenProbe();
        secondAcquired.Should().BeFalse();
    }

    [Fact]
    public void HalfOpenProbe_Success_ResetsCircuitToClosed()
    {
        var endpoint = CreateEndpoint();
        for (int i = 0; i < FailureThreshold; i++)
        {
            endpoint.RecordDeliveryFailure(FailureThreshold, _openDuration, _baseTime);
        }
        endpoint.EvaluateCircuitState(_openDuration, _baseTime.AddSeconds(65));
        endpoint.TryAcquireHalfOpenProbe();

        // Probe succeeds
        var successTime = _baseTime.AddSeconds(70);
        endpoint.RecordDeliverySuccess(successTime);

        endpoint.CircuitState.Should().Be(EndpointCircuitState.Closed);
        endpoint.ConsecutiveFailureCount.Should().Be(0);
        endpoint.CircuitOpenedAtUtc.Should().BeNull();
        endpoint.CircuitHalfOpenProbeActive.Should().BeFalse();
    }

    [Fact]
    public void HalfOpenProbe_Failure_TripsBackToOpenImmediately()
    {
        var endpoint = CreateEndpoint();
        for (int i = 0; i < FailureThreshold; i++)
        {
            endpoint.RecordDeliveryFailure(FailureThreshold, _openDuration, _baseTime);
        }
        endpoint.EvaluateCircuitState(_openDuration, _baseTime.AddSeconds(65));
        endpoint.TryAcquireHalfOpenProbe();

        // Probe fails immediately on first failure
        var failureTime = _baseTime.AddSeconds(70);
        endpoint.RecordDeliveryFailure(FailureThreshold, _openDuration, failureTime);

        endpoint.CircuitState.Should().Be(EndpointCircuitState.Open);
        endpoint.CircuitOpenedAtUtc.Should().Be(failureTime);
        endpoint.CircuitHalfOpenProbeActive.Should().BeFalse();
    }

    private WebhookEndpoint CreateEndpoint()
    {
        return new WebhookEndpoint(
            Guid.NewGuid(),
            _tenantId,
            "Test Circuit Endpoint",
            "https://api.customer.com/webhook",
            "protected-secret",
            _baseTime);
    }
}
