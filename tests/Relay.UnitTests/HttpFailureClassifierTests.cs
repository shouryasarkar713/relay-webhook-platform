using System.Net.Sockets;
using System.Security.Authentication;
using FluentAssertions;
using Relay.Application.Services;
using Relay.Domain.Enums;
using Xunit;

namespace Relay.UnitTests;

public sealed class HttpFailureClassifierTests
{
    private readonly WebhookDeliveryFailureClassifier _classifier = new();

    [Theory]
    [InlineData(200)]
    [InlineData(201)]
    [InlineData(204)]
    public void ClassifyStatusCode_2xxSuccess_ReturnsSuccessOutcome(int statusCode)
    {
        var result = _classifier.ClassifyStatusCode(statusCode);

        result.IsSuccess.Should().BeTrue();
        result.IsRetryable.Should().BeFalse();
        result.Outcome.Should().Be(DeliveryAttemptOutcome.Succeeded);
        result.CountsTowardCircuitBreaker.Should().BeFalse();
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(307)]
    [InlineData(308)]
    public void ClassifyStatusCode_3xxRedirect_ReturnsPermanentFailure(int statusCode)
    {
        var result = _classifier.ClassifyStatusCode(statusCode);

        result.IsSuccess.Should().BeFalse();
        result.IsRetryable.Should().BeFalse();
        result.Outcome.Should().Be(DeliveryAttemptOutcome.PermanentHttpFailure);
        result.CountsTowardCircuitBreaker.Should().BeFalse();
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(410)]
    [InlineData(422)]
    public void ClassifyStatusCode_4xxClientError_ReturnsPermanentFailure(int statusCode)
    {
        var result = _classifier.ClassifyStatusCode(statusCode);

        result.IsSuccess.Should().BeFalse();
        result.IsRetryable.Should().BeFalse();
        result.Outcome.Should().Be(DeliveryAttemptOutcome.PermanentHttpFailure);
        result.CountsTowardCircuitBreaker.Should().BeFalse();
    }

    [Theory]
    [InlineData(408)]
    [InlineData(425)]
    [InlineData(429)]
    public void ClassifyStatusCode_Retryable4xx_ReturnsRetryableHttpFailure(int statusCode)
    {
        var result = _classifier.ClassifyStatusCode(statusCode);

        result.IsSuccess.Should().BeFalse();
        result.IsRetryable.Should().BeTrue();
        result.Outcome.Should().Be(DeliveryAttemptOutcome.RetryableHttpFailure);

        if (statusCode == 408)
        {
            result.CountsTowardCircuitBreaker.Should().BeTrue("Request timeout indicates endpoint responsiveness issue");
        }
        else
        {
            result.CountsTowardCircuitBreaker.Should().BeFalse("429 rate limiting should not trip circuit breaker");
        }
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public void ClassifyStatusCode_5xxServerError_ReturnsRetryableHttpFailureAndCountsForCircuit(int statusCode)
    {
        var result = _classifier.ClassifyStatusCode(statusCode);

        result.IsSuccess.Should().BeFalse();
        result.IsRetryable.Should().BeTrue();
        result.Outcome.Should().Be(DeliveryAttemptOutcome.RetryableHttpFailure);
        result.CountsTowardCircuitBreaker.Should().BeTrue();
    }

    [Fact]
    public void ClassifyException_CancellationDuringShutdown_ReturnsWorkerInterrupted()
    {
        var ex = new OperationCanceledException();
        var result = _classifier.ClassifyException(ex, isCancellationRequested: true);

        result.IsSuccess.Should().BeFalse();
        result.IsRetryable.Should().BeTrue();
        result.Outcome.Should().Be(DeliveryAttemptOutcome.WorkerInterrupted);
        result.ErrorCategory.Should().Be("WorkerShutdown");
        result.CountsTowardCircuitBreaker.Should().BeFalse();
    }

    [Fact]
    public void ClassifyException_TimeoutException_ReturnsTimeoutAndCountsForCircuit()
    {
        var ex = new TimeoutException("The HTTP request timed out.");
        var result = _classifier.ClassifyException(ex, isCancellationRequested: false);

        result.IsSuccess.Should().BeFalse();
        result.IsRetryable.Should().BeTrue();
        result.Outcome.Should().Be(DeliveryAttemptOutcome.Timeout);
        result.ErrorCategory.Should().Be("RequestTimeout");
        result.CountsTowardCircuitBreaker.Should().BeTrue();
    }

    [Fact]
    public void ClassifyException_TlsOrSslError_ReturnsPermanentFailure()
    {
        var ex = new AuthenticationException("The remote certificate is invalid according to the validation procedure.");
        var result = _classifier.ClassifyException(ex, isCancellationRequested: false);

        result.IsSuccess.Should().BeFalse();
        result.IsRetryable.Should().BeFalse();
        result.Outcome.Should().Be(DeliveryAttemptOutcome.PermanentHttpFailure);
        result.ErrorCategory.Should().Be("TlsCertificateError");
        result.CountsTowardCircuitBreaker.Should().BeFalse();
    }

    [Fact]
    public void ClassifyException_SsrfViolation_ReturnsPermanentFailure()
    {
        var ex = new HttpRequestException("Blocked private IP address (SSRF Protection)");
        var result = _classifier.ClassifyException(ex, isCancellationRequested: false);

        result.IsSuccess.Should().BeFalse();
        result.IsRetryable.Should().BeFalse();
        result.Outcome.Should().Be(DeliveryAttemptOutcome.PermanentHttpFailure);
        result.ErrorCategory.Should().Be("SsrfBlocked");
        result.CountsTowardCircuitBreaker.Should().BeFalse();
    }

    [Fact]
    public void ClassifyException_TransientNetworkFailure_ReturnsRetryableNetworkFailure()
    {
        var ex = new HttpRequestException("Connection refused", new SocketException((int)SocketError.ConnectionRefused));
        var result = _classifier.ClassifyException(ex, isCancellationRequested: false);

        result.IsSuccess.Should().BeFalse();
        result.IsRetryable.Should().BeTrue();
        result.Outcome.Should().Be(DeliveryAttemptOutcome.NetworkFailure);
        result.ErrorCategory.Should().Be("ConnectionRefused");
        result.CountsTowardCircuitBreaker.Should().BeTrue();
    }
}
