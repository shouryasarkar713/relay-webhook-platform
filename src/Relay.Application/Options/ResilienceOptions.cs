namespace Relay.Application.Options;

public sealed class DeliveryRetryOptions
{
    public const string SectionName = "DeliveryRetry";

    public int MaxAttempts { get; set; } = 8;
    public double BaseDelaySeconds { get; set; } = 5.0;
    public double MaxDelaySeconds { get; set; } = 3600.0;
    public int MaxRetryWindowHours { get; set; } = 24;
    public double MaxServerRequestedDelaySeconds { get; set; } = 86400.0;
}

public sealed class CircuitBreakerOptions
{
    public const string SectionName = "CircuitBreaker";

    public int FailureThreshold { get; set; } = 5;
    public int OpenDurationSeconds { get; set; } = 60;
    public int HalfOpenProbeLimit { get; set; } = 1;
    public int ProbeLeaseDurationSeconds { get; set; } = 30;
}

public sealed class BulkheadOptions
{
    public const string SectionName = "Bulkhead";

    public int MaxGlobalConcurrency { get; set; } = 20;
    public int MaxPerEndpointConcurrency { get; set; } = 5;
}
