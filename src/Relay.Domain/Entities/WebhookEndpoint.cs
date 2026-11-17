using Relay.Domain.Enums;

namespace Relay.Domain.Entities;

public sealed class WebhookEndpoint
{
    private readonly List<WebhookEndpointSubscription> _subscriptions = new();

    private WebhookEndpoint() { }

    public WebhookEndpoint(
        Guid id,
        Guid tenantId,
        string name,
        string url,
        string signingSecretProtected,
        DateTimeOffset createdAtUtc,
        bool isActive = true)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name cannot be empty.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("URL cannot be empty.", nameof(url));
        }

        if (string.IsNullOrWhiteSpace(signingSecretProtected))
        {
            throw new ArgumentException("SigningSecretProtected cannot be empty.", nameof(signingSecretProtected));
        }

        Id = id;
        TenantId = tenantId;
        Name = name.Trim();
        Url = url.Trim();
        SigningSecretProtected = signingSecretProtected;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
        IsActive = isActive;
        CircuitState = EndpointCircuitState.Closed;
        ConsecutiveFailureCount = 0;
        CircuitHalfOpenProbeActive = false;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Url { get; private set; } = string.Empty;
    public bool IsActive { get; private set; }
    public string SigningSecretProtected { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public DateTimeOffset? DisabledAtUtc { get; private set; }
    public DateTimeOffset? LastDeliveryAtUtc { get; private set; }

    // Circuit Breaker State
    public EndpointCircuitState CircuitState { get; private set; }
    public int ConsecutiveFailureCount { get; private set; }
    public DateTimeOffset? CircuitOpenedAtUtc { get; private set; }
    public bool CircuitHalfOpenProbeActive { get; private set; }
    public Guid? HalfOpenProbeToken { get; private set; }
    public DateTimeOffset? HalfOpenProbeLeaseExpiresAtUtc { get; private set; }

    public Tenant? Tenant { get; private set; }
    public IReadOnlyCollection<WebhookEndpointSubscription> Subscriptions => _subscriptions.AsReadOnly();

    public void Update(string name, string url, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name cannot be empty.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("URL cannot be empty.", nameof(url));
        }

        Name = name.Trim();
        Url = url.Trim();
        UpdatedAtUtc = now;
    }

    public void Deactivate(DateTimeOffset now)
    {
        IsActive = false;
        DisabledAtUtc = now;
        UpdatedAtUtc = now;
    }

    public void RecordDeliveryAttempt(DateTimeOffset now)
    {
        LastDeliveryAtUtc = now;
        UpdatedAtUtc = now;
    }

    public void RecordDeliverySuccess(DateTimeOffset now, Guid? expectedProbeToken = null)
    {
        if (CircuitState == EndpointCircuitState.HalfOpen && expectedProbeToken.HasValue && HalfOpenProbeToken != expectedProbeToken)
        {
            // Fenced out by newer probe lease owner
            return;
        }

        LastDeliveryAtUtc = now;
        UpdatedAtUtc = now;
        ConsecutiveFailureCount = 0;
        CircuitState = EndpointCircuitState.Closed;
        CircuitOpenedAtUtc = null;
        CircuitHalfOpenProbeActive = false;
        HalfOpenProbeToken = null;
        HalfOpenProbeLeaseExpiresAtUtc = null;
    }

    public void RecordDeliveryFailure(
        int failureThreshold,
        TimeSpan openDuration,
        DateTimeOffset now,
        Guid? expectedProbeToken = null)
    {
        UpdatedAtUtc = now;

        if (CircuitState == EndpointCircuitState.HalfOpen)
        {
            if (expectedProbeToken.HasValue && HalfOpenProbeToken != expectedProbeToken)
            {
                // Fenced out by newer probe lease owner
                return;
            }

            // Probe failed: trip circuit back to Open immediately
            CircuitState = EndpointCircuitState.Open;
            CircuitOpenedAtUtc = now;
            CircuitHalfOpenProbeActive = false;
            HalfOpenProbeToken = null;
            HalfOpenProbeLeaseExpiresAtUtc = null;
            return;
        }

        ConsecutiveFailureCount++;
        if (ConsecutiveFailureCount >= failureThreshold)
        {
            CircuitState = EndpointCircuitState.Open;
            CircuitOpenedAtUtc = now;
            CircuitHalfOpenProbeActive = false;
            HalfOpenProbeToken = null;
            HalfOpenProbeLeaseExpiresAtUtc = null;
        }
    }

    public void EvaluateCircuitState(TimeSpan openDuration, DateTimeOffset now)
    {
        if (CircuitState == EndpointCircuitState.Open && CircuitOpenedAtUtc.HasValue)
        {
            if (now >= CircuitOpenedAtUtc.Value.Add(openDuration))
            {
                CircuitState = EndpointCircuitState.HalfOpen;
                CircuitHalfOpenProbeActive = false;
                HalfOpenProbeToken = null;
                HalfOpenProbeLeaseExpiresAtUtc = null;
                UpdatedAtUtc = now;
            }
        }
    }

    public bool TryAcquireHalfOpenProbe(Guid probeToken, TimeSpan leaseDuration, DateTimeOffset now)
    {
        if (CircuitState == EndpointCircuitState.HalfOpen)
        {
            var isAvailable = !HalfOpenProbeToken.HasValue ||
                              !HalfOpenProbeLeaseExpiresAtUtc.HasValue ||
                              now >= HalfOpenProbeLeaseExpiresAtUtc.Value;

            if (isAvailable)
            {
                HalfOpenProbeToken = probeToken;
                HalfOpenProbeLeaseExpiresAtUtc = now.Add(leaseDuration);
                CircuitHalfOpenProbeActive = true;
                UpdatedAtUtc = now;
                return true;
            }
        }

        return false;
    }

    public bool TryAcquireHalfOpenProbe()
    {
        return TryAcquireHalfOpenProbe(Guid.NewGuid(), TimeSpan.FromSeconds(30), DateTimeOffset.UtcNow);
    }

    public void ResetCircuit(DateTimeOffset now)
    {
        CircuitState = EndpointCircuitState.Closed;
        ConsecutiveFailureCount = 0;
        CircuitOpenedAtUtc = null;
        CircuitHalfOpenProbeActive = false;
        HalfOpenProbeToken = null;
        HalfOpenProbeLeaseExpiresAtUtc = null;
        UpdatedAtUtc = now;
    }

    public void SetSubscriptions(IEnumerable<string> eventTypes)
    {
        _subscriptions.Clear();
        foreach (var eventType in eventTypes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            _subscriptions.Add(new WebhookEndpointSubscription(Guid.NewGuid(), Id, eventType));
        }
    }
}
