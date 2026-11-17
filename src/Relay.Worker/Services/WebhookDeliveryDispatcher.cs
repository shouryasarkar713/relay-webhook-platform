using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Relay.Application.Abstractions;
using Relay.Application.Options;
using Relay.Domain.Entities;
using Relay.Domain.Enums;
using Relay.Infrastructure.Security;
using Relay.Worker.Options;

namespace Relay.Worker.Services;

public sealed class WebhookDeliveryDispatcher
{
    private readonly IWebhookDeliveryStore _deliveryStore;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IEndpointSecretProtector _secretProtector;
    private readonly IWebhookSigner _signer;
    private readonly IWebhookDeliveryFailureClassifier _failureClassifier;
    private readonly IRetryAfterParser _retryAfterParser;
    private readonly IWebhookRetryPolicy _retryPolicy;
    private readonly IEndpointCircuitBreakerManager _circuitBreakerManager;
    private readonly IBulkheadManager _bulkheadManager;
    private readonly TimeProvider _timeProvider;
    private readonly OutboxDispatcherOptions _dispatcherOptions;
    private readonly WebhookSecurityOptions _securityOptions;
    private readonly DeliveryRetryOptions _retryOptions;
    private readonly CircuitBreakerOptions _circuitBreakerOptions;
    private readonly ILogger<WebhookDeliveryDispatcher> _logger;

    public WebhookDeliveryDispatcher(
        IWebhookDeliveryStore deliveryStore,
        IHttpClientFactory httpClientFactory,
        IEndpointSecretProtector secretProtector,
        IWebhookSigner signer,
        IWebhookDeliveryFailureClassifier failureClassifier,
        IRetryAfterParser retryAfterParser,
        IWebhookRetryPolicy retryPolicy,
        IEndpointCircuitBreakerManager circuitBreakerManager,
        IBulkheadManager bulkheadManager,
        TimeProvider timeProvider,
        IOptions<OutboxDispatcherOptions> dispatcherOptions,
        IOptions<WebhookSecurityOptions> securityOptions,
        IOptions<DeliveryRetryOptions> retryOptions,
        IOptions<CircuitBreakerOptions> circuitBreakerOptions,
        ILogger<WebhookDeliveryDispatcher> logger)
    {
        _deliveryStore = deliveryStore;
        _httpClientFactory = httpClientFactory;
        _secretProtector = secretProtector;
        _signer = signer;
        _failureClassifier = failureClassifier;
        _retryAfterParser = retryAfterParser;
        _retryPolicy = retryPolicy;
        _circuitBreakerManager = circuitBreakerManager;
        _bulkheadManager = bulkheadManager;
        _timeProvider = timeProvider;
        _dispatcherOptions = dispatcherOptions.Value;
        _securityOptions = securityOptions.Value;
        _retryOptions = retryOptions.Value;
        _circuitBreakerOptions = circuitBreakerOptions.Value;
        _logger = logger;
    }

    public async Task<int> ProcessDeliveriesAsync(CancellationToken cancellationToken = default)
    {
        var leaseToken = Guid.NewGuid();
        var leaseDuration = _dispatcherOptions.LeaseDuration;

        var deliveries = await _deliveryStore.ClaimPendingDeliveriesAsync(
            _dispatcherOptions.BatchSize,
            leaseToken,
            leaseDuration,
            cancellationToken);

        if (deliveries.Count == 0)
        {
            return 0;
        }

        _logger.LogInformation("Claimed {Count} webhook deliveries for dispatch under lease {LeaseToken}", deliveries.Count, leaseToken);

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = _dispatcherOptions.MaxConcurrency,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(deliveries, parallelOptions, async (delivery, ct) =>
        {
            try
            {
                await DispatchSingleDeliveryAsync(delivery, leaseToken, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error dispatching delivery {DeliveryId}", delivery.Id);
            }
        });

        return deliveries.Count;
    }

    private async Task DispatchSingleDeliveryAsync(
        WebhookDelivery delivery,
        Guid leaseToken,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();

        // 1. Check if endpoint is inactive or deleted
        if (delivery.Endpoint == null || !delivery.Endpoint.IsActive)
        {
            _logger.LogWarning("Endpoint {EndpointId} is inactive or deleted. Marking delivery {DeliveryId} as Skipped.", delivery.EndpointId, delivery.Id);

            var attempt = new WebhookDeliveryAttempt(
                Guid.NewGuid(),
                delivery.Id,
                delivery.AttemptCount + 1,
                now,
                now,
                0,
                null,
                DeliveryAttemptOutcome.Started,
                "EndpointInactive",
                "Endpoint deactivated before dispatch");

            await _deliveryStore.CompleteDeliveryAttemptAsync(
                delivery.Id,
                leaseToken,
                attempt,
                WebhookDeliveryStatus.Skipped,
                nextAttemptAtUtc: null,
                deadLetteredAtUtc: null,
                errorCategory: "EndpointInactive",
                errorMessage: "Endpoint is inactive",
                cancellationToken);
            return;
        }

        // 2. Check Circuit Breaker State (Endpoint-Level with Probe Fencing)
        var circuitDecision = await _circuitBreakerManager.CanExecuteDecisionAsync(delivery.EndpointId, cancellationToken);
        if (!circuitDecision.CanExecute)
        {
            _logger.LogInformation("Circuit open or probe active for endpoint {EndpointId}. Deferring delivery {DeliveryId}.", delivery.EndpointId, delivery.Id);
            var deferDelay = TimeSpan.FromSeconds(_circuitBreakerOptions.OpenDurationSeconds);
            await _deliveryStore.DeferDeliveryForCircuitBreakerAsync(
                delivery.Id,
                leaseToken,
                now.Add(deferDelay),
                $"Endpoint circuit is open or probing (next check in {deferDelay.TotalSeconds:F0}s)",
                cancellationToken);
            return;
        }

        // 3. Acquire Concurrency Bulkhead Slot (Per-Endpoint first, then Global Worker)
        using var bulkheadLease = await _bulkheadManager.AcquireSlotAsync(delivery.EndpointId, cancellationToken);

        // 4. Construct Canonical Webhook Payload
        using var rawDoc = JsonDocument.Parse(delivery.Event!.PayloadJson);
        var envelopeObj = new
        {
            schemaVersion = 1,
            eventId = delivery.EventId,
            eventType = delivery.Event.EventType,
            occurredAtUtc = delivery.Event.CreatedAtUtc,
            payload = rawDoc.RootElement.Clone()
        };

        var bodyBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelopeObj));

        // 5. Decrypt signing secret
        string rawSecret;
        try
        {
            rawSecret = _secretProtector.Unprotect(delivery.Endpoint.SigningSecretProtected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decrypt signing secret for endpoint {EndpointId}", delivery.EndpointId);
            var attempt = new WebhookDeliveryAttempt(
                Guid.NewGuid(),
                delivery.Id,
                delivery.AttemptCount + 1,
                now,
                now,
                0,
                null,
                DeliveryAttemptOutcome.PermanentHttpFailure,
                "DecryptionError",
                "Failed to decrypt signing secret");

            await _deliveryStore.CompleteDeliveryAttemptAsync(
                delivery.Id,
                leaseToken,
                attempt,
                WebhookDeliveryStatus.Failed,
                nextAttemptAtUtc: null,
                deadLetteredAtUtc: null,
                errorCategory: "DecryptionError",
                errorMessage: "Signing secret decryption failure",
                cancellationToken);
            return;
        }

        // 6. Compute HMAC-SHA256 signature
        var signature = _signer.Sign(rawSecret, bodyBytes);

        // 7. Begin Delivery Attempt (Atomic state update in DB before network I/O)
        var attemptId = Guid.NewGuid();
        var startedAt = _timeProvider.GetUtcNow();
        var nextAttemptNumber = delivery.AttemptCount + 1;

        var initialAttempt = new WebhookDeliveryAttempt(
            attemptId,
            delivery.Id,
            nextAttemptNumber,
            startedAt,
            startedAt,
            0,
            null,
            DeliveryAttemptOutcome.Started,
            null,
            null);

        var attemptBegun = await _deliveryStore.BeginDeliveryAttemptAsync(
            delivery.Id,
            leaseToken,
            initialAttempt,
            cancellationToken);

        if (!attemptBegun)
        {
            _logger.LogWarning("Fencing check failed when beginning attempt for delivery {DeliveryId}", delivery.Id);
            return;
        }

        // 8. Prepare HTTP Request
        var client = _httpClientFactory.CreateClient("SecureWebhookClient");
        using var request = new HttpRequestMessage(HttpMethod.Post, delivery.Endpoint.Url)
        {
            Content = new ByteArrayContent(bodyBytes)
        };

        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Headers.Add("X-Relay-Event-Id", delivery.EventId.ToString("D"));
        request.Headers.Add("X-Relay-Delivery-Id", delivery.Id.ToString("D"));
        request.Headers.Add("X-Relay-Attempt", nextAttemptNumber.ToString());
        request.Headers.Add("X-Relay-Timestamp", signature.Timestamp.ToString());
        request.Headers.Add("X-Relay-Signature", signature.HeaderValue);

        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage? response = null;
        DeliveryFailureClassification classification;
        string? responseExcerpt = null;
        int? statusCode = null;
        ParsedRetryAfter? parsedRetryAfter = null;

        try
        {
            using var timeoutCts = new CancellationTokenSource(_securityOptions.RequestTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);
            stopwatch.Stop();
            statusCode = (int)response.StatusCode;

            // Bounded stream reader prevents memory exhaustion, bounded by linked cancellation token
            responseExcerpt = await BoundedResponseReader.ReadExcerptAsync(
                response.Content,
                _securityOptions.MaxResponseBytes,
                linkedCts.Token);

            // Parse Retry-After header if present
            var maxHorizon = TimeSpan.FromSeconds(_retryOptions.MaxServerRequestedDelaySeconds);
            parsedRetryAfter = _retryAfterParser.Parse(response.Headers, maxHorizon);

            classification = _failureClassifier.ClassifyStatusCode(statusCode.Value);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            classification = _failureClassifier.ClassifyException(ex, cancellationToken.IsCancellationRequested);
            responseExcerpt = ex.Message;
        }

        var completedAt = _timeProvider.GetUtcNow();

        // 9. Update Circuit Breaker based on outcome with probe fencing
        if (classification.IsSuccess)
        {
            await _circuitBreakerManager.RecordSuccessAsync(delivery.EndpointId, circuitDecision.ProbeToken, cancellationToken);
        }
        else if (classification.CountsTowardCircuitBreaker)
        {
            await _circuitBreakerManager.RecordFailureAsync(delivery.EndpointId, circuitDecision.ProbeToken, cancellationToken);
        }

        // 10. Determine Final Delivery Status & Next Attempt
        WebhookDeliveryStatus targetStatus;
        DateTimeOffset? nextAttemptAtUtc = null;
        DateTimeOffset? deadLetteredAtUtc = null;
        string? failureErrorMessage = null;

        if (classification.IsSuccess)
        {
            targetStatus = WebhookDeliveryStatus.Delivered;
        }
        else if (classification.IsRetryable)
        {
            var nextRetryCycleAttempt = delivery.RetryCycleAttemptCount + 1;
            var retryCycleStart = delivery.RetryCycleStartedAtUtc == default ? delivery.CreatedAtUtc : delivery.RetryCycleStartedAtUtc;

            var retryDecision = _retryPolicy.EvaluateRetry(nextRetryCycleAttempt, retryCycleStart, parsedRetryAfter);

            if (retryDecision.ShouldRetry)
            {
                targetStatus = WebhookDeliveryStatus.Pending;
                nextAttemptAtUtc = retryDecision.NextAttemptAtUtc;
                failureErrorMessage = $"{classification.Description} - Scheduled retry at {nextAttemptAtUtc:O}";
            }
            else
            {
                // Retry budget exhausted or window expired
                targetStatus = WebhookDeliveryStatus.DeadLettered;
                deadLetteredAtUtc = completedAt;
                failureErrorMessage = retryDecision.TerminalReason ?? classification.Description;
            }
        }
        else
        {
            // Permanent failure (e.g. 400, 401, 403, 404, SSRF blocked)
            targetStatus = WebhookDeliveryStatus.Failed;
            failureErrorMessage = classification.Description;
        }

        // 11. Complete Attempt and Record in Database
        var completedAttempt = new WebhookDeliveryAttempt(
            attemptId,
            delivery.Id,
            nextAttemptNumber,
            startedAt,
            completedAt,
            stopwatch.ElapsedMilliseconds,
            statusCode,
            classification.Outcome,
            classification.ErrorCategory,
            responseExcerpt);

        await _deliveryStore.CompleteDeliveryAttemptAsync(
            delivery.Id,
            leaseToken,
            completedAttempt,
            targetStatus,
            nextAttemptAtUtc,
            deadLetteredAtUtc,
            classification.ErrorCategory,
            failureErrorMessage,
            cancellationToken);
    }
}
