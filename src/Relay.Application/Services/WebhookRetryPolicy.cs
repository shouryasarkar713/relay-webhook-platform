using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Relay.Application.Abstractions;
using Relay.Application.Options;

namespace Relay.Application.Services;

public sealed class WebhookRetryPolicy : IWebhookRetryPolicy
{
    private readonly DeliveryRetryOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Func<double> _jitterRandomSource;

    public WebhookRetryPolicy(
        IOptions<DeliveryRetryOptions> options,
        TimeProvider timeProvider,
        Func<double>? jitterRandomSource = null)
    {
        _options = options.Value;
        _timeProvider = timeProvider;
        _jitterRandomSource = jitterRandomSource ?? (() => RandomNumberGenerator.GetInt32(0, int.MaxValue) / (double)int.MaxValue);
    }

    public RetryDecision EvaluateRetry(
        int attemptCount,
        DateTimeOffset deliveryCreatedAtUtc,
        ParsedRetryAfter? serverRetryAfter)
    {
        var now = _timeProvider.GetUtcNow();

        // 1. Check max attempt limit
        if (attemptCount >= _options.MaxAttempts)
        {
            return new RetryDecision(
                ShouldRetry: false,
                NextAttemptAtUtc: null,
                Delay: TimeSpan.Zero,
                TerminalReason: $"Retry budget exhausted after {attemptCount} attempts (MaxAttempts={_options.MaxAttempts})");
        }

        // 2. Check server-requested Retry-After exceeds maximum horizon
        if (serverRetryAfter != null && serverRetryAfter.ExceedsMaxHorizon)
        {
            return new RetryDecision(
                ShouldRetry: false,
                NextAttemptAtUtc: null,
                Delay: TimeSpan.Zero,
                TerminalReason: $"Server-requested Retry-After ({serverRetryAfter.RequestedDelay.TotalSeconds:F0}s) exceeds maximum allowed horizon ({_options.MaxServerRequestedDelaySeconds}s)");
        }

        // 3. Compute Full Jitter exponential backoff delay
        // ExponentialCap = min(MaxDelay, BaseDelay * 2^(attemptNumber - 1))
        var exponent = Math.Min(30, Math.Max(0, attemptCount - 1));
        var multiplier = Math.Pow(2, exponent);
        var exponentialCap = Math.Min(_options.MaxDelaySeconds, _options.BaseDelaySeconds * multiplier);

        // Full Jitter: UniformRandom(0, ExponentialCap)
        var jitter = _jitterRandomSource();
        var calculatedDelaySeconds = jitter * exponentialCap;

        // 4. Combine with server Retry-After (must not retry earlier than server requested)
        var serverDelaySeconds = serverRetryAfter?.RequestedDelay.TotalSeconds ?? 0.0;
        var finalDelaySeconds = Math.Max(calculatedDelaySeconds, serverDelaySeconds);

        var finalDelay = TimeSpan.FromSeconds(finalDelaySeconds);
        var targetTimeUtc = now.Add(finalDelay);

        // 5. Check if target time exceeds maximum retry window
        var maxRetryWindow = TimeSpan.FromHours(_options.MaxRetryWindowHours);
        if (targetTimeUtc > deliveryCreatedAtUtc.Add(maxRetryWindow))
        {
            return new RetryDecision(
                ShouldRetry: false,
                NextAttemptAtUtc: null,
                Delay: TimeSpan.Zero,
                TerminalReason: $"Retry window of {_options.MaxRetryWindowHours} hours expired since event delivery creation ({deliveryCreatedAtUtc:O})");
        }

        return new RetryDecision(
            ShouldRetry: true,
            NextAttemptAtUtc: targetTimeUtc,
            Delay: finalDelay,
            TerminalReason: null);
    }
}
