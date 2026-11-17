using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Relay.Application.Abstractions;
using Relay.Application.Options;
using Relay.Domain.Enums;

namespace Relay.Infrastructure.Services;

public sealed class EndpointCircuitBreakerManager : IEndpointCircuitBreakerManager
{
    private readonly RelayDbContext _dbContext;
    private readonly TimeProvider _timeProvider;
    private readonly CircuitBreakerOptions _options;
    private readonly ILogger<EndpointCircuitBreakerManager> _logger;

    public EndpointCircuitBreakerManager(
        RelayDbContext dbContext,
        TimeProvider timeProvider,
        IOptions<CircuitBreakerOptions> options,
        ILogger<EndpointCircuitBreakerManager> logger)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<bool> CanExecuteAsync(Guid endpointId, CancellationToken cancellationToken = default)
    {
        var decision = await CanExecuteDecisionAsync(endpointId, cancellationToken);
        return decision.CanExecute;
    }

    public async Task<CircuitExecutionDecision> CanExecuteDecisionAsync(
        Guid endpointId,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var openDuration = TimeSpan.FromSeconds(_options.OpenDurationSeconds);

        var stateInfo = await _dbContext.WebhookEndpoints
            .AsNoTracking()
            .Where(e => e.Id == endpointId)
            .Select(e => new
            {
                e.CircuitState,
                e.CircuitOpenedAtUtc,
                e.HalfOpenProbeToken,
                e.HalfOpenProbeLeaseExpiresAtUtc
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (stateInfo is null)
        {
            return new CircuitExecutionDecision(false, false, null);
        }

        if (stateInfo.CircuitState == EndpointCircuitState.Closed)
        {
            return new CircuitExecutionDecision(true, false, null);
        }

        if (stateInfo.CircuitState == EndpointCircuitState.Open)
        {
            if (stateInfo.CircuitOpenedAtUtc.HasValue &&
                now < stateInfo.CircuitOpenedAtUtc.Value.Add(openDuration))
            {
                // Open period has not yet expired
                return new CircuitExecutionDecision(false, false, null);
            }
        }

        // Open duration has expired, or state is already HalfOpen.
        // Attempt atomic acquisition of probe lease.
        var probeToken = Guid.NewGuid();
        var probeLeaseDuration = TimeSpan.FromSeconds(_options.ProbeLeaseDurationSeconds);
        var probeExpiresAt = now.Add(probeLeaseDuration);

        var rowsAffected = await _dbContext.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE WebhookEndpoints
               SET CircuitState = 'HalfOpen',
                   CircuitHalfOpenProbeActive = 1,
                   HalfOpenProbeToken = {probeToken},
                   HalfOpenProbeLeaseExpiresAtUtc = {probeExpiresAt},
                   UpdatedAtUtc = {now}
               WHERE Id = {endpointId} AND (
                   (CircuitState = 'HalfOpen' AND (HalfOpenProbeToken IS NULL OR HalfOpenProbeLeaseExpiresAtUtc IS NULL OR HalfOpenProbeLeaseExpiresAtUtc <= {now}))
                   OR
                   (CircuitState = 'Open' AND CircuitOpenedAtUtc IS NOT NULL AND DATEADD(second, {_options.OpenDurationSeconds}, CircuitOpenedAtUtc) <= {now})
               )", cancellationToken);

        if (rowsAffected > 0)
        {
            _logger.LogInformation(
                "Circuit Breaker probe acquired for endpoint {EndpointId} with token {ProbeToken}, lease expires at {ExpiresAt}",
                endpointId,
                probeToken,
                probeExpiresAt);
            return new CircuitExecutionDecision(true, true, probeToken);
        }

        // Probe already actively claimed by another worker and lease has not expired
        return new CircuitExecutionDecision(false, false, null);
    }

    public Task RecordSuccessAsync(Guid endpointId, CancellationToken cancellationToken = default)
        => RecordSuccessAsync(endpointId, null, cancellationToken);

    public async Task RecordSuccessAsync(
        Guid endpointId,
        Guid? probeToken,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        if (probeToken.HasValue)
        {
            var rowsAffected = await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                $@"UPDATE WebhookEndpoints
                   SET CircuitState = 'Closed',
                       ConsecutiveFailureCount = 0,
                       CircuitOpenedAtUtc = NULL,
                       CircuitHalfOpenProbeActive = 0,
                       HalfOpenProbeToken = NULL,
                       HalfOpenProbeLeaseExpiresAtUtc = NULL,
                       LastDeliveryAtUtc = {now},
                       UpdatedAtUtc = {now}
                   WHERE Id = {endpointId} AND (CircuitState = 'HalfOpen' AND HalfOpenProbeToken = {probeToken})",
                cancellationToken);

            if (rowsAffected > 0)
            {
                _logger.LogInformation(
                    "Circuit Breaker probe succeeded with token {ProbeToken}. Circuit reset to CLOSED for endpoint {EndpointId}",
                    probeToken,
                    endpointId);
            }
            else
            {
                _logger.LogWarning(
                    "Circuit Breaker probe success rejected for endpoint {EndpointId}: probe token {ProbeToken} was fenced out or expired",
                    endpointId,
                    probeToken);
            }
        }
        else
        {
            await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                $@"UPDATE WebhookEndpoints
                   SET ConsecutiveFailureCount = 0,
                       LastDeliveryAtUtc = {now},
                       UpdatedAtUtc = {now}
                   WHERE Id = {endpointId}",
                cancellationToken);
        }
    }

    public Task RecordFailureAsync(Guid endpointId, CancellationToken cancellationToken = default)
        => RecordFailureAsync(endpointId, null, cancellationToken);

    public async Task RecordFailureAsync(
        Guid endpointId,
        Guid? probeToken,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        if (probeToken.HasValue)
        {
            var rowsAffected = await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                $@"UPDATE WebhookEndpoints
                   SET CircuitState = 'Open',
                       CircuitOpenedAtUtc = {now},
                       CircuitHalfOpenProbeActive = 0,
                       HalfOpenProbeToken = NULL,
                       HalfOpenProbeLeaseExpiresAtUtc = NULL,
                       UpdatedAtUtc = {now}
                   WHERE Id = {endpointId} AND CircuitState = 'HalfOpen' AND HalfOpenProbeToken = {probeToken}",
                cancellationToken);

            if (rowsAffected > 0)
            {
                _logger.LogWarning(
                    "Circuit Breaker probe failed with token {ProbeToken}. Circuit tripped back to OPEN for endpoint {EndpointId}",
                    probeToken,
                    endpointId);
            }
            else
            {
                _logger.LogWarning(
                    "Circuit Breaker probe failure rejected for endpoint {EndpointId}: probe token {ProbeToken} was fenced out or expired",
                    endpointId,
                    probeToken);
            }
        }
        else
        {
            var rowsAffected = await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                $@"UPDATE WebhookEndpoints
                   SET ConsecutiveFailureCount = ConsecutiveFailureCount + 1,
                       CircuitState = CASE 
                           WHEN CircuitState = 'HalfOpen' THEN 'Open'
                           WHEN ConsecutiveFailureCount + 1 >= {_options.FailureThreshold} THEN 'Open' 
                           ELSE CircuitState 
                       END,
                       CircuitOpenedAtUtc = CASE 
                           WHEN CircuitState = 'HalfOpen' THEN {now}
                           WHEN ConsecutiveFailureCount + 1 >= {_options.FailureThreshold} THEN {now} 
                           ELSE CircuitOpenedAtUtc 
                       END,
                       CircuitHalfOpenProbeActive = 0,
                       HalfOpenProbeToken = NULL,
                       HalfOpenProbeLeaseExpiresAtUtc = NULL,
                       UpdatedAtUtc = {now}
                   WHERE Id = {endpointId} AND CircuitState IN ('Closed', 'HalfOpen')",
                cancellationToken);

            if (rowsAffected > 0)
            {
                var isNowOpen = await _dbContext.WebhookEndpoints
                    .AsNoTracking()
                    .Where(e => e.Id == endpointId && e.CircuitState == EndpointCircuitState.Open)
                    .AnyAsync(cancellationToken);

                if (isNowOpen)
                {
                    _logger.LogWarning(
                        "Circuit Breaker tripped to OPEN for endpoint {EndpointId}. Threshold={Threshold}, OpenDuration={OpenDuration}s",
                        endpointId,
                        _options.FailureThreshold,
                        _options.OpenDurationSeconds);
                }
            }
        }
    }
}
