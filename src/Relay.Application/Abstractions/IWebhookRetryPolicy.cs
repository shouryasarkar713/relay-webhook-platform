using Relay.Domain.Entities;

namespace Relay.Application.Abstractions;

public sealed record RetryDecision(
    bool ShouldRetry,
    DateTimeOffset? NextAttemptAtUtc,
    TimeSpan Delay,
    string? TerminalReason);

public interface IWebhookRetryPolicy
{
    RetryDecision EvaluateRetry(
        int attemptCount,
        DateTimeOffset deliveryCreatedAtUtc,
        ParsedRetryAfter? serverRetryAfter);
}
