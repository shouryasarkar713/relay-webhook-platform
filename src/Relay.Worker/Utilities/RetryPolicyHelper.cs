namespace Relay.Worker.Utilities;

public static class RetryPolicyHelper
{
    /// <summary>
    /// Computes conventional Full Jitter exponential backoff:
    /// exponentialCap = min(maxDelay, baseDelay * 2^(attempt - 1))
    /// actualDelay = uniform random distribution in [0, exponentialCap]
    /// </summary>
    /// <param name="attemptCount">1-based attempt count.</param>
    /// <param name="baseDelay">Initial backoff interval.</param>
    /// <param name="maxDelay">Ceiling cap for backoff interval.</param>
    /// <param name="randomUniformFraction">Optional random double in [0.0, 1.0] for deterministic testing. Defaults to Cryptographic random.</param>
    public static TimeSpan CalculateFullJitterBackoff(
        int attemptCount,
        TimeSpan baseDelay,
        TimeSpan maxDelay,
        double? randomUniformFraction = null)
    {
        if (attemptCount <= 0)
        {
            attemptCount = 1;
        }

        // Calculate exponential factor: 2^(attempt - 1)
        var factor = Math.Pow(2, Math.Min(attemptCount - 1, 30));
        var rawMilliseconds = baseDelay.TotalMilliseconds * factor;

        // Cap at max delay
        var exponentialCap = Math.Min(rawMilliseconds, maxDelay.TotalMilliseconds);

        // Apply conventional Full Jitter: random in [0, exponentialCap]
        var fraction = randomUniformFraction.HasValue
            ? Math.Clamp(randomUniformFraction.Value, 0.0, 1.0)
            : Random.Shared.NextDouble();

        var jitteredMilliseconds = exponentialCap * fraction;

        return TimeSpan.FromMilliseconds(jitteredMilliseconds);
    }
}
