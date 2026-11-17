using System.Net.Sockets;
using System.Security.Authentication;
using Relay.Application.Abstractions;
using Relay.Domain.Enums;

namespace Relay.Application.Services;

public sealed class WebhookDeliveryFailureClassifier : IWebhookDeliveryFailureClassifier
{
    public DeliveryFailureClassification ClassifyStatusCode(int statusCode)
    {
        // 2xx Success
        if (statusCode is >= 200 and <= 299)
        {
            return new DeliveryFailureClassification(
                IsSuccess: true,
                IsRetryable: false,
                Outcome: DeliveryAttemptOutcome.Succeeded,
                ErrorCategory: null,
                Description: $"HTTP {statusCode} Success",
                CountsTowardCircuitBreaker: false);
        }

        // 3xx Redirection (Redirects are disabled; treated as permanent failure)
        if (statusCode is >= 300 and <= 399)
        {
            return new DeliveryFailureClassification(
                IsSuccess: false,
                IsRetryable: false,
                Outcome: DeliveryAttemptOutcome.PermanentHttpFailure,
                ErrorCategory: $"HttpStatus{statusCode}",
                Description: $"HTTP {statusCode} Redirect Not Allowed",
                CountsTowardCircuitBreaker: false);
        }

        // Retryable 4xx codes: 408 (Request Timeout), 425 (Too Early), 429 (Too Many Requests)
        if (statusCode is 408 or 425 or 429)
        {
            return new DeliveryFailureClassification(
                IsSuccess: false,
                IsRetryable: true,
                Outcome: DeliveryAttemptOutcome.RetryableHttpFailure,
                ErrorCategory: $"HttpStatus{statusCode}",
                Description: $"HTTP {statusCode} Retryable Error",
                // 429 rate limits should not trip circuit breakers blindly; 408 indicates endpoint timeout
                CountsTowardCircuitBreaker: statusCode == 408);
        }

        // Permanent 4xx client errors (400, 401, 403, 404, 409, 410, 422, etc.)
        if (statusCode is >= 400 and <= 499)
        {
            return new DeliveryFailureClassification(
                IsSuccess: false,
                IsRetryable: false,
                Outcome: DeliveryAttemptOutcome.PermanentHttpFailure,
                ErrorCategory: $"HttpStatus{statusCode}",
                Description: $"HTTP {statusCode} Permanent Client Error",
                CountsTowardCircuitBreaker: false);
        }

        // 5xx Server errors (500, 502, 503, 504, etc.)
        if (statusCode is >= 500 and <= 599)
        {
            return new DeliveryFailureClassification(
                IsSuccess: false,
                IsRetryable: true,
                Outcome: DeliveryAttemptOutcome.RetryableHttpFailure,
                ErrorCategory: $"HttpStatus{statusCode}",
                Description: $"HTTP {statusCode} Server Error",
                CountsTowardCircuitBreaker: true);
        }

        // Unknown status code outside 200-599
        return new DeliveryFailureClassification(
            IsSuccess: false,
            IsRetryable: false,
            Outcome: DeliveryAttemptOutcome.PermanentHttpFailure,
            ErrorCategory: $"HttpStatus{statusCode}",
            Description: $"HTTP {statusCode} Unexpected Status Code",
            CountsTowardCircuitBreaker: false);
    }

    public DeliveryFailureClassification ClassifyException(Exception exception, bool isCancellationRequested)
    {
        if (isCancellationRequested)
        {
            return new DeliveryFailureClassification(
                IsSuccess: false,
                IsRetryable: true,
                Outcome: DeliveryAttemptOutcome.WorkerInterrupted,
                ErrorCategory: "WorkerShutdown",
                Description: "Worker execution was interrupted by host shutdown.",
                CountsTowardCircuitBreaker: false);
        }

        if (exception is OperationCanceledException or TimeoutException)
        {
            return new DeliveryFailureClassification(
                IsSuccess: false,
                IsRetryable: true,
                Outcome: DeliveryAttemptOutcome.Timeout,
                ErrorCategory: "RequestTimeout",
                Description: "HTTP request timed out.",
                CountsTowardCircuitBreaker: true);
        }

        if (exception is AuthenticationException ||
            exception.InnerException is AuthenticationException ||
            exception.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase))
        {
            return new DeliveryFailureClassification(
                IsSuccess: false,
                IsRetryable: false,
                Outcome: DeliveryAttemptOutcome.PermanentHttpFailure,
                ErrorCategory: "TlsCertificateError",
                Description: "TLS handshake or certificate verification failed.",
                CountsTowardCircuitBreaker: false);
        }

        if ((exception is InvalidOperationException || exception is HttpRequestException) &&
            (exception.Message.Contains("SSRF", StringComparison.OrdinalIgnoreCase) ||
             exception.Message.Contains("prohibited", StringComparison.OrdinalIgnoreCase)))
        {
            return new DeliveryFailureClassification(
                IsSuccess: false,
                IsRetryable: false,
                Outcome: DeliveryAttemptOutcome.PermanentHttpFailure,
                ErrorCategory: "SsrfBlocked",
                Description: "Destination was blocked by SSRF defense.",
                CountsTowardCircuitBreaker: false);
        }

        if (exception is SocketException || exception.InnerException is SocketException)
        {
            var sockEx = (exception as SocketException) ?? (exception.InnerException as SocketException);
            var isConnectionRefused = sockEx?.SocketErrorCode == SocketError.ConnectionRefused;
            return new DeliveryFailureClassification(
                IsSuccess: false,
                IsRetryable: true,
                Outcome: DeliveryAttemptOutcome.NetworkFailure,
                ErrorCategory: isConnectionRefused ? "ConnectionRefused" : $"SocketError_{sockEx?.SocketErrorCode}",
                Description: $"Socket error: {sockEx?.SocketErrorCode}",
                CountsTowardCircuitBreaker: true);
        }

        if (exception is HttpRequestException httpEx)
        {
            return new DeliveryFailureClassification(
                IsSuccess: false,
                IsRetryable: true,
                Outcome: DeliveryAttemptOutcome.NetworkFailure,
                ErrorCategory: "HttpRequestException",
                Description: httpEx.Message,
                CountsTowardCircuitBreaker: true);
        }

        return new DeliveryFailureClassification(
            IsSuccess: false,
            IsRetryable: true,
            Outcome: DeliveryAttemptOutcome.UnknownOutcome,
            ErrorCategory: exception.GetType().Name,
            Description: exception.Message,
            CountsTowardCircuitBreaker: false);
    }
}
