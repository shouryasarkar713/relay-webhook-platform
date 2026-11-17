namespace Relay.Application.Abstractions;

public sealed record CircuitExecutionDecision(
    bool CanExecute,
    bool IsProbe,
    Guid? ProbeToken);

public interface IEndpointCircuitBreakerManager
{
    Task<bool> CanExecuteAsync(Guid endpointId, CancellationToken cancellationToken = default);
    Task<CircuitExecutionDecision> CanExecuteDecisionAsync(Guid endpointId, CancellationToken cancellationToken = default);
    Task RecordSuccessAsync(Guid endpointId, CancellationToken cancellationToken = default);
    Task RecordSuccessAsync(Guid endpointId, Guid? probeToken, CancellationToken cancellationToken = default);
    Task RecordFailureAsync(Guid endpointId, CancellationToken cancellationToken = default);
    Task RecordFailureAsync(Guid endpointId, Guid? probeToken, CancellationToken cancellationToken = default);
}
