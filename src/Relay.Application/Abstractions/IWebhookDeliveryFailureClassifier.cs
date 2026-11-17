using System.Security.Authentication;
using Relay.Domain.Enums;

namespace Relay.Application.Abstractions;

public sealed record DeliveryFailureClassification(
    bool IsSuccess,
    bool IsRetryable,
    DeliveryAttemptOutcome Outcome,
    string? ErrorCategory,
    string Description,
    bool CountsTowardCircuitBreaker);

public interface IWebhookDeliveryFailureClassifier
{
    DeliveryFailureClassification ClassifyStatusCode(int statusCode);
    DeliveryFailureClassification ClassifyException(Exception exception, bool isCancellationRequested);
}
